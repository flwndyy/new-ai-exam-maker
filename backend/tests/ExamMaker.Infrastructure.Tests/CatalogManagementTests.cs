using System.Text.Json;
using ExamMaker.Application.Ai;
using ExamMaker.Domain.Ai;
using ExamMaker.Domain.Catalog;
using ExamMaker.Infrastructure.Ai;
using Npgsql;
using ExamMaker.Application.Catalog;
using ExamMaker.Domain.Questions;
using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Catalog;
using ExamMaker.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamMaker.Infrastructure.Tests;

public sealed class CatalogManagementTests : IAsyncLifetime
{
    private readonly PostgresFixture fixture = new();
    private readonly Guid actorId = Guid.NewGuid();
    private static readonly Guid DepartmentId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid SubjectId = Guid.Parse("20000000-0000-0000-0000-000000000001");
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    /// <summary>Use the real EF implementation with synthetic audit actors in an isolated database.</summary>
    private static CatalogManagement Service(ExamMakerDbContext db) => new(db, NullLogger<CatalogManagement>.Instance);

    /// <summary>Provision a local administrator without calling Cognito or touching development data.</summary>
    private async Task SeedAdminAsync()
    {
        await using var db = fixture.CreateContext();
        db.Users.Add(new User
        {
            Id = actorId,
            CognitoSub = "catalog-test-admin",
            Email = "catalog@example.invalid",
            FullName = "Synthetic Admin",
            Role = "admin"
        });
        await db.SaveChangesAsync();
    }

    [PostgresFact]
    // Verify complete CRUD, alternate-key updates and all nine committed audit events.
    public async Task Catalog_crud_preserves_contract_fields_and_audits_each_successful_mutation()
    {
        await SeedAdminAsync();
        await using var db = fixture.CreateContext();
        var service = Service(db);
        var department = await service.SaveDepartmentAsync(actorId, null, new("  Synthetic Department  "), default);
        Assert.Equal("Synthetic Department", department.Name);
        var subject = await service.SaveSubjectAsync(actorId, null, new("Synthetic Subject", department.Id), default);
        var topic = await service.SaveTopicAsync(actorId, null, new(subject.Id, 10, "Synthetic Chapter", 0), default);
        await service.SaveDepartmentAsync(actorId, department.Id, new("Renamed Department"), default);
        // An unreferenced alternate-key column must be writable rather than rejected by EF's change tracker.
        subject = await service.SaveSubjectAsync(actorId, subject.Id, new("Renamed Subject", DepartmentId), default);
        topic = await service.SaveTopicAsync(actorId, topic.Id, new(SubjectId, 12, "Renamed Chapter", 7), default);
        Assert.Equal(DepartmentId, subject.DepartmentId);
        Assert.Equal(12, topic.Grade);
        await using (var verify = fixture.CreateContext())
        {
            Assert.Equal(SubjectId, (await verify.Topics.SingleAsync(x => x.Id == topic.Id)).SubjectId);
            Assert.Equal(DepartmentId, (await verify.Subjects.SingleAsync(x => x.Id == subject.Id)).DepartmentId);
        }
        await service.DeleteTopicAsync(actorId, topic.Id, default);
        await service.DeleteSubjectAsync(actorId, subject.Id, default);
        await service.DeleteDepartmentAsync(actorId, department.Id, default);
        await using var final = fixture.CreateContext();
        var audits = await final.AuditLogs.Where(x => x.ActorId == actorId).ToListAsync();
        Assert.Equal(12, audits.Count);
        Assert.Contains(audits, x => x.Action == "UpdateSubject" && x.DepartmentId == department.Id && !x.IsSecondary);
        Assert.Contains(audits, x => x.Action == "UpdateSubject" && x.DepartmentId == DepartmentId && x.IsSecondary);
        Assert.Contains(audits, x => x.EntityId == topic.Id && x.Action == "UpdateTopic" && x.DepartmentId == department.Id && !x.IsSecondary);
        Assert.Contains(audits, x => x.EntityId == topic.Id && x.Action == "UpdateTopic" && x.DepartmentId == DepartmentId && x.IsSecondary);
        Assert.Contains(audits, x => x.Action == "DeleteDepartment" && x.Note!.Contains("Renamed Department"));
        Assert.Contains(audits, x => x.Action == "DeleteSubject" && x.Note!.Contains("Renamed Subject") && x.Note.Contains("department="));
        Assert.Contains(audits, x => x.Action == "DeleteTopic" && x.Note!.Contains("Renamed Chapter") && x.Note.Contains("grade=12"));
        Assert.All(audits, x => Assert.False(string.IsNullOrWhiteSpace(x.Note)));
        Assert.Contains(audits, x => x.EntityId == topic.Id && x.Action == "UpdateTopic" && x.Note!.Contains("grade=10") && x.Note.Contains("grade=12"));
        Assert.False(await final.Topics.AnyAsync(x => x.Id == topic.Id));
    }

    [PostgresFact]
    // Exercise filters, stable ordering, page totals and large-but-valid empty pages against PostgreSQL.
    public async Task Lists_filter_and_paginate_seeded_catalog_in_documented_order()
    {
        await using var db = fixture.CreateContext();
        var service = Service(db);
        var departments = await service.DepartmentsAsync(new(PageSize: 2), default);
        Assert.Equal(4, departments.TotalItems);
        Assert.Equal(2, departments.TotalPages);
        Assert.Equal(2, departments.Items.Count);
        Assert.Empty((await service.DepartmentsAsync(new(Page: 3, PageSize: 2), default)).Items);
        var subjects = await service.SubjectsAsync(new(DepartmentId: DepartmentId), default);
        Assert.All(subjects.Items, x => Assert.Equal(DepartmentId, x.DepartmentId));
        Assert.Equal(SubjectId, Assert.Single(subjects.Items).Id);
        var topics = await service.TopicsAsync(new(PageSize: 100, SubjectId: SubjectId, Grade: 10), default);
        Assert.NotEmpty(topics.Items);
        Assert.All(topics.Items, x => { Assert.Equal(SubjectId, x.SubjectId); Assert.Equal(10, x.Grade); });
        Assert.Equal(topics.Items.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => x.Id), topics.Items.Select(x => x.Id));
        var empty = await service.TopicsAsync(new(SubjectId: Guid.NewGuid()), default);
        Assert.Equal(0, empty.TotalItems);
        Assert.Equal(0, empty.TotalPages);
        Assert.Empty(empty.Items);
        foreach (var query in new[] { new CatalogQuery(Page: 0), new(PageSize: 101), new(Grade: 9), new(Page: int.MaxValue), new(SubjectId: Guid.Empty) })
            Assert.Equal(400, (await Assert.ThrowsAsync<CatalogException>(() => service.TopicsAsync(query, default))).StatusCode);
    }

    [PostgresFact]
    // Reject malformed direct use-case calls, not only MVC validation failures.
    public async Task Invalid_names_parents_grades_and_sort_orders_do_not_write_or_audit()
    {
        await SeedAdminAsync();
        await using var db = fixture.CreateContext();
        var service = Service(db);
        foreach (var name in new[] { " ", new string('x', 201), "bad\0name" })
            Assert.Equal(400, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveDepartmentAsync(actorId, null, new(name), default))).StatusCode);
        foreach (var parent in new Guid?[] { null, Guid.Empty, Guid.NewGuid() })
            Assert.Equal(400, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveSubjectAsync(actorId, null, new("New", parent), default))).StatusCode);
        foreach (var input in new[] { new TopicInput(SubjectId, null, "New", 0), new(SubjectId, 9, "New", 0),
            new(SubjectId, 10, "New", null), new(SubjectId, 10, "New", -1), new(null, 10, "New", 0), new(Guid.NewGuid(), 10, "New", 0) })
            Assert.Equal(400, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveTopicAsync(actorId, null, input, default))).StatusCode);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [PostgresFact]
    // Preserve the schema's exact uniqueness scope and translate violations without persisting failure audits.
    public async Task Duplicate_names_conflict_only_within_the_existing_unique_scope()
    {
        await SeedAdminAsync();
        DepartmentView department;
        await using (var db = fixture.CreateContext())
        {
            var service = Service(db);
            department = await service.SaveDepartmentAsync(actorId, null, new("Unique Department"), default);
            await service.SaveSubjectAsync(actorId, null, new("Shared Subject", department.Id), default);
            await service.SaveSubjectAsync(actorId, null, new("Shared Subject", DepartmentId), default);
            await service.SaveTopicAsync(actorId, null, new(SubjectId, 10, "Shared Chapter", 1), default);
            await service.SaveTopicAsync(actorId, null, new(SubjectId, 11, "Shared Chapter", 1), default);
        }
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).SaveDepartmentAsync(actorId, null, new(" Unique Department "), default))).StatusCode);
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).SaveSubjectAsync(actorId, null, new("Shared Subject", department.Id), default))).StatusCode);
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).SaveTopicAsync(actorId, null, new(SubjectId, 10, "Shared Chapter", 1), default))).StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(5, await verify.AuditLogs.CountAsync());
    }

    [PostgresFact]
    // Verify FK RESTRICT conflicts for every catalog level and referenced alternate-key changes.
    public async Task Referenced_deletions_and_inconsistent_subject_or_grade_moves_roll_back()
    {
        await SeedAdminAsync();
        Guid topicId;
        await using (var db = fixture.CreateContext())
        {
            topicId = await db.Topics.Where(x => x.SubjectId == SubjectId && x.Grade == 10).Select(x => x.Id).FirstAsync();
            db.Questions.Add(new Question
            {
                Id = Guid.NewGuid(),
                SubjectId = SubjectId,
                DepartmentId = DepartmentId,
                TopicId = topicId,
                Grade = 10,
                Type = "MultipleChoice",
                Difficulty = "Recognition",
                Stem = "Synthetic",
                CreatedBy = actorId
            });
            await db.SaveChangesAsync();
        }
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).DeleteDepartmentAsync(actorId, DepartmentId, default))).StatusCode);
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).DeleteSubjectAsync(actorId, SubjectId, default))).StatusCode);
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).DeleteTopicAsync(actorId, topicId, default))).StatusCode);
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).SaveSubjectAsync(actorId, SubjectId,
                new("Moved", Guid.Parse("10000000-0000-0000-0000-000000000002")), default))).StatusCode);
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).SaveTopicAsync(actorId, topicId, new(SubjectId, 12, "Moved", 0), default))).StatusCode);
        await using (var db = fixture.CreateContext())
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).SaveTopicAsync(actorId, topicId,
                new(Guid.Parse("20000000-0000-0000-0000-000000000002"), 10, "Moved", 0), default))).StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.Equal(10, (await verify.Topics.SingleAsync(x => x.Id == topicId)).Grade);
        Assert.Equal(DepartmentId, (await verify.Subjects.SingleAsync(x => x.Id == SubjectId)).DepartmentId);
        Assert.Empty(await verify.AuditLogs.ToListAsync());
    }

    [PostgresFact]
    // Distinguish missing mutation targets from invalid parent data.
    public async Task Missing_update_and_delete_targets_return_not_found_for_all_entity_types()
    {
        await SeedAdminAsync();
        await using var db = fixture.CreateContext();
        var service = Service(db);
        var missing = Guid.NewGuid();
        Assert.Equal(404, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveDepartmentAsync(actorId, missing, new("Missing"), default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveSubjectAsync(actorId, missing, new("Missing", DepartmentId), default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveTopicAsync(actorId, missing, new(SubjectId, 10, "Missing", 0), default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<CatalogException>(() => service.DeleteDepartmentAsync(actorId, missing, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<CatalogException>(() => service.DeleteSubjectAsync(actorId, missing, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<CatalogException>(() => service.DeleteTopicAsync(actorId, missing, default))).StatusCode);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [PostgresFact]
    // Lock acquisition must be bounded and must not leave a partial mutation or audit behind.
    public async Task Busy_administration_lock_returns_conflict_without_writing()
    {
        await SeedAdminAsync();
        await using var holder = fixture.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48683401)");
        await using var db = fixture.CreateContext();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(db).SaveDepartmentAsync(actorId, null, new("Busy"), watchdog.Token))).StatusCode);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [PostgresFact]
    // A user-management downgrade committed while catalog waits must invalidate the request's former admin role.
    public async Task Administrator_role_is_rechecked_after_waiting_for_the_shared_user_lock()
    {
        await SeedAdminAsync();
        await using var holder = fixture.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48683401)");
        await holder.Users.Where(x => x.Id == actorId).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        await using var db = fixture.CreateContext();
        await db.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)db.Database.GetDbConnection()).ProcessID;
        var pending = Service(db).SaveDepartmentAsync(actorId, null, new("Must Not Exist"), default);
        await WaitForLockAsync(holder, pid, 48683401);
        await transaction.CommitAsync();
        Assert.Equal(403, (await Assert.ThrowsAsync<CatalogException>(() => pending)).StatusCode);
        Assert.Empty(await db.AuditLogs.ToListAsync());
        Assert.False(await db.Departments.AnyAsync(x => x.Name == "Must Not Exist"));
    }
    [PostgresFact]
    // Direct service callers cannot bypass the administrator check by supplying an arbitrary actor ID.
    public async Task Non_admin_or_missing_actors_cannot_mutate_any_catalog_entity()
    {
        await SeedAdminAsync();
        await using var db = fixture.CreateContext();
        await db.Users.Where(x => x.Id == actorId).ExecuteUpdateAsync(x => x.SetProperty(u => u.Role, "teacher")
            .SetProperty(u => u.DepartmentId, DepartmentId));
        var service = Service(db);
        Assert.Equal(403, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveDepartmentAsync(actorId, null, new("Denied"), default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveSubjectAsync(actorId, null, new("Denied", DepartmentId), default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveTopicAsync(actorId, null, new(SubjectId, 10, "Denied", 0), default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<CatalogException>(() => service.DeleteTopicAsync(Guid.NewGuid(), Guid.NewGuid(), default))).StatusCode);
        Assert.Empty(await db.AuditLogs.ToListAsync());
    }

    [PostgresFact]
    // Force audit persistence to fail after an UPDATE and verify that the same transaction rolls the update back.
    public async Task Audit_failure_rolls_back_the_catalog_change()
    {
        await SeedAdminAsync();
        string original;
        await using (var db = fixture.CreateContext()) original = (await db.Departments.SingleAsync(x => x.Id == DepartmentId)).Name;
        var options = new DbContextOptionsBuilder<ExamMakerDbContext>().UseNpgsql(fixture.ConnectionString)
            .AddInterceptors(new RejectCatalogAudit()).Options;
        await using (var db = new ExamMakerDbContext(options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).SaveDepartmentAsync(actorId, DepartmentId, new("Rolled Back"), default));
        await using var verify = fixture.CreateContext();
        Assert.Equal(original, (await verify.Departments.SingleAsync(x => x.Id == DepartmentId)).Name);
        Assert.Empty(await verify.AuditLogs.ToListAsync());
    }

    [PostgresFact]
    // Visually identical decomposed names must hit the same unique index after NFC normalization.
    public async Task Unicode_equivalent_department_names_conflict_after_normalization()
    {
        await SeedAdminAsync();
        await using (var db = fixture.CreateContext())
            await Service(db).SaveDepartmentAsync(actorId, null, new("Tổ thử Toán"), default);
        await using var duplicate = fixture.CreateContext();
        Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => Service(duplicate).SaveDepartmentAsync(actorId, null,
            new("Tổ thử Toán".Normalize(System.Text.NormalizationForm.FormD)), default))).StatusCode);
    }

    /// <summary>Observe an actual waiter so concurrency tests cannot pass merely because operations ran sequentially.</summary>
    private static async Task WaitForLockAsync(ExamMakerDbContext observer, int pid, int key)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.8));
        while (!await observer.Database.SqlQuery<bool>($"""
            SELECT EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory'
                AND pid = {pid} AND classid = 0 AND objid = {key}::oid AND NOT granted) AS "Value"
            """).SingleAsync(timeout.Token))
            await Task.Delay(10, timeout.Token);
    }

    [PostgresFact]
    // Active JSON references protect otherwise unreferenced chapters and parent subject identity.
    public async Task Active_jobs_block_catalog_deletion_and_moves_but_allow_label_changes()
    {
        await SeedAdminAsync();
        foreach (var status in new[] { "Queued", "Running" })
        {
            Guid topicId;
            await using (var db = fixture.CreateContext())
            {
                var topic = await Service(db).SaveTopicAsync(actorId, null, new(SubjectId, 10, "Job " + status, 0), default);
                topicId = topic.Id;
                db.AiGenerationJobs.Add(new AiGenerationJob
                {
                    Id = Guid.NewGuid(),
                    RequestedBy = actorId,
                    Params = JsonSerializer.Serialize(new { subjectId = SubjectId, topicId }),
                    Status = status,
                    LeaseExpiresAt = status == "Running" ? DateTime.UtcNow.AddMinutes(1) : null
                });
                await db.SaveChangesAsync();
            }
            await using var attempt = fixture.CreateContext();
            var service = Service(attempt);
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => service.DeleteTopicAsync(actorId, topicId, default))).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveTopicAsync(actorId, topicId,
                new(SubjectId, 11, "Moved", 0), default))).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveTopicAsync(actorId, topicId,
                new(Guid.Parse("20000000-0000-0000-0000-000000000002"), 10, "Moved", 0), default))).StatusCode);
            Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => service.SaveSubjectAsync(actorId, SubjectId,
                new("Moved", Guid.Parse("10000000-0000-0000-0000-000000000002")), default))).StatusCode);
            await service.SaveTopicAsync(actorId, topicId, new(SubjectId, 10, "Renamed " + status, 1), default);
        }
    }

    [PostgresFact]
    // JSON-only subjects are protected too; historical terminal jobs do not permanently pin the catalog.
    public async Task Subject_json_references_block_deletion_until_jobs_are_terminal()
    {
        await SeedAdminAsync();
        await using var db = fixture.CreateContext();
        var service = Service(db);
        var subject = await service.SaveSubjectAsync(actorId, null, new("Job Subject", DepartmentId), default);
        var job = new AiGenerationJob
        {
            Id = Guid.NewGuid(),
            RequestedBy = actorId,
            Params = JsonSerializer.Serialize(new { subjectId = subject.Id }),
            Status = "Queued"
        };
        db.AiGenerationJobs.Add(job);
        await db.SaveChangesAsync();
        Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => service.DeleteSubjectAsync(actorId, subject.Id, default))).StatusCode);
        await db.AiGenerationJobs.Where(x => x.Id == job.Id).ExecuteUpdateAsync(x => x.SetProperty(j => j.Status, "Succeeded")
            .SetProperty(j => j.FinishedAt, DateTime.UtcNow));
        await service.DeleteSubjectAsync(actorId, subject.Id, default);
        foreach (var status in new[] { "Succeeded", "Failed" })
        {
            var topic = await service.SaveTopicAsync(actorId, null, new(SubjectId, 10, "Terminal " + status, 0), default);
            db.AiGenerationJobs.Add(new AiGenerationJob
            {
                Id = Guid.NewGuid(),
                RequestedBy = actorId,
                Params = JsonSerializer.Serialize(new { subjectId = SubjectId, topicId = topic.Id }),
                Status = status,
                FinishedAt = DateTime.UtcNow,
                ErrorCode = status == "Failed" ? "Synthetic" : null
            });
            await db.SaveChangesAsync();
            await service.SaveTopicAsync(actorId, topic.Id, new(SubjectId, 11, "Moved " + status, 0), default);
            await service.DeleteTopicAsync(actorId, topic.Id, default);
        }
    }

    [PostgresFact]
    // If a catalog transaction wins the race, submission revalidates after waiting without consuming quota.
    public async Task Job_creation_rechecks_catalog_after_waiting_for_a_catalog_mutation()
    {
        await SeedAdminAsync();
        await using var holder = fixture.CreateContext();
        var topic = await Service(holder).SaveTopicAsync(actorId, null, new(SubjectId, 10, "Race", 0), default);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48683402)");
        await holder.Topics.Where(x => x.Id == topic.Id).ExecuteDeleteAsync();
        await using var submit = fixture.CreateContext();
        await submit.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)submit.Database.GetDbConnection()).ProcessID;
        var pending = new EfAiJobStore(submit).TryCreateQueuedJobAsync(actorId,
            new(SubjectId, topic.Id, 10, "MultipleChoice", "Recognition", 1, null), DateOnly.FromDateTime(DateTime.UtcNow), 10, 3, default);
        await WaitForLockAsync(holder, pid, 48683402);
        await transaction.CommitAsync();
        await Assert.ThrowsAsync<AiCatalogChangedException>(() => pending);
        Assert.False(await submit.AiGenerationJobs.AnyAsync());
        Assert.False(await submit.AiUsageDaily.AnyAsync());
    }

    [PostgresFact]
    // If job creation wins the race, A5 waits for its commit and then rejects the newly protected deletion.
    public async Task Catalog_mutation_observes_a_job_committed_while_waiting_for_the_catalog_lock()
    {
        await SeedAdminAsync();
        await using var holder = fixture.CreateContext();
        var topic = await Service(holder).SaveTopicAsync(actorId, null, new(SubjectId, 10, "Job Race", 0), default);
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock_shared(48683402)");
        holder.AiGenerationJobs.Add(new AiGenerationJob
        {
            Id = Guid.NewGuid(),
            RequestedBy = actorId,
            Params = JsonSerializer.Serialize(new { subjectId = SubjectId, topicId = topic.Id })
        });
        await holder.SaveChangesAsync();
        await using var attempt = fixture.CreateContext();
        await attempt.Database.OpenConnectionAsync();
        var pid = ((NpgsqlConnection)attempt.Database.GetDbConnection()).ProcessID;
        var pending = Service(attempt).DeleteTopicAsync(actorId, topic.Id, default);
        await WaitForLockAsync(holder, pid, 48683402);
        await transaction.CommitAsync();
        Assert.Equal(409, (await Assert.ThrowsAsync<CatalogException>(() => pending)).StatusCode);
        Assert.True(await attempt.Topics.AnyAsync(x => x.Id == topic.Id));
    }

    private sealed class RejectCatalogAudit : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        /// <summary>Inject an audit-write failure after the service has already executed its catalog UPDATE.</summary>
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic audit failure.");
    }

}
