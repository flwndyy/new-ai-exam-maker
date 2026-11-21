using ExamMaker.Domain.Catalog;
using System.Text.Json;
using ExamMaker.Application.Questions;
using ExamMaker.Domain.Ai;
using ExamMaker.Domain.Exams;
using ExamMaker.Domain.Questions;
using ExamMaker.Domain.QuestionSets;
using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Persistence;
using ExamMaker.Infrastructure.Questions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamMaker.Infrastructure.Tests;

public sealed class QuestionManagementTests : IAsyncLifetime
{
    private readonly PostgresFixture fixture = new();
    private readonly Guid owner = Guid.NewGuid(), head = Guid.NewGuid(), otherHead = Guid.NewGuid(), admin = Guid.NewGuid(), outsider = Guid.NewGuid();
    private static readonly Guid Department = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Subject = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Topic = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private const string Payload = """{"options":[{"label":"A","text":"One"},{"label":"B","text":"Two"},{"label":"C","text":"Three"},{"label":"D","text":"Four"}],"correctLabel":"A"}""";
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private static QuestionManagement Service(ExamMakerDbContext db) => new(db, NullLogger<QuestionManagement>.Instance);
    private static QuestionInput Input(string stem = "Synthetic question") => new(Subject, Topic, 10, "MultipleChoice", "Recognition", stem, JsonSerializer.Deserialize<JsonElement>(Payload));

    /// <summary>Seed synthetic authorization profiles in a disposable database; never call Cognito from integration tests.</summary>
    private async Task SeedAsync()
    {
        await using var db = fixture.CreateContext();
        foreach (var (id, role, department) in new (Guid, string, Guid?)[]
        { (owner, "teacher", Department), (head, "head", Department), (otherHead, "head", Department), (admin, "admin", null),
            (outsider, "head", Guid.Parse("10000000-0000-0000-0000-000000000002")) })
            db.Users.Add(new User
            {
                Id = id,
                CognitoSub = id.ToString(),
                Email = id + "@example.invalid",
                FullName = "Synthetic " + role,
                Role = role,
                DepartmentId = department
            });
        await db.SaveChangesAsync();
    }

    /// <summary>Create a question through the production use case with an independent request-scoped DbContext.</summary>
    private async Task<QuestionView> CreateAsync(Guid? creator = null)
    {
        await using var db = fixture.CreateContext();
        return await Service(db).CreateAsync(creator ?? owner, Input(), default);
    }

    private async Task<QuestionView> ActAsync(Guid actor, Guid id, QuestionAction action, string? note = null)
    {
        await using var db = fixture.CreateContext();
        var submittedAt = await db.Questions.AsNoTracking().Where(x => x.Id == id).Select(x => x.SubmittedAt).SingleAsync();
        return await Service(db).TransitionAsync(actor, id, action, note, default,
            submittedAt is DateTime at ? new DateTimeOffset(at) : null);
    }

    [PostgresFact]
    // Both edit and transition timestamps must advance after an actual PostgreSQL round trip at sub-microsecond clock intervals.
    public async Task Mutation_timestamps_advance_at_postgres_precision()
    {
        await SeedAsync();
        var created = await CreateAsync();
        var clock = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using var db = fixture.CreateContext();
        var q = await db.Questions.SingleAsync(x => x.Id == created.Id);
        q.UpdatedAt = clock;
        await db.SaveChangesAsync();
        var actor = new ExamMaker.Application.Authentication.AuthenticatedUser(owner, owner.ToString(),
            "synthetic@example.invalid", "Synthetic", "teacher", Department, null, true);
        foreach (var offset in new[] { 0, 1, 9, -10 })
        {
            await db.Entry(q).ReloadAsync();
            var before = q.UpdatedAt;
            Assert.True(QuestionRules.Edit(actor, q, Input("Changed " + offset), before.AddTicks(offset)));
            await db.SaveChangesAsync();
            await db.Entry(q).ReloadAsync();
            Assert.Equal(before.AddTicks(TimeSpan.TicksPerMicrosecond), q.UpdatedAt);
            q.Status = "Submitted"; q.SubmittedAt = q.UpdatedAt;
            before = q.UpdatedAt;
            Assert.True(QuestionRules.Transition(actor, q, QuestionAction.Withdraw, null, before.AddTicks(offset)));
            await db.SaveChangesAsync();
            await db.Entry(q).ReloadAsync();
            Assert.Equal(before.AddTicks(TimeSpan.TicksPerMicrosecond), q.UpdatedAt);
        }
    }

    [PostgresFact]
    // Actual permission and state checks must win over a missing review token for both decisions.
    public async Task Missing_review_tokens_are_checked_after_visibility_authority_and_state()
    {
        await SeedAsync();
        var q = await CreateAsync(head);
        await ActAsync(head, q.Id, QuestionAction.Submit);
        foreach (var action in new[] { QuestionAction.Approve, QuestionAction.Reject })
        {
            await using var db = fixture.CreateContext();
            Assert.Equal("NotFound", (await Assert.ThrowsAsync<QuestionException>(() =>
                Service(db).TransitionAsync(outsider, q.Id, action, "Reason", default))).Code);
            Assert.Equal("SelfReview", (await Assert.ThrowsAsync<QuestionException>(() =>
                Service(db).TransitionAsync(head, q.Id, action, "Reason", default))).Code);
            Assert.Equal("submittedAt", (await Assert.ThrowsAsync<QuestionException>(() =>
                Service(db).TransitionAsync(otherHead, q.Id, action, "Reason", default))).Field);
        }
        await ActAsync(otherHead, q.Id, QuestionAction.Approve);
        foreach (var action in new[] { QuestionAction.Approve, QuestionAction.Reject })
        {
            await using var db = fixture.CreateContext();
            Assert.Equal("InvalidStateTransition", (await Assert.ThrowsAsync<QuestionException>(() =>
                Service(db).TransitionAsync(otherHead, q.Id, action, "Reason", default))).Code);
        }
    }

    [PostgresFact]
    // Exercise rejection/resubmission, metadata clearing, approval, no-op PUT and approved revision reopening.
    public async Task Review_rounds_and_content_revisions_are_audited_and_persisted()
    {
        await SeedAsync();
        var question = await CreateAsync();
        Assert.Equal("Draft", question.Status); Assert.Equal("Manual", question.Source); Assert.Equal(1, question.Revision);
        Assert.Equal(Department, question.DepartmentId); Assert.Equal(owner, question.CreatedBy.Id);
        Assert.Equal(question.CreatedAt, question.UpdatedAt);
        await ActAsync(owner, question.Id, QuestionAction.Submit);
        Assert.Equal(400, (await Assert.ThrowsAsync<QuestionException>(() => ActAsync(head, question.Id, QuestionAction.Reject, " "))).StatusCode);
        question = await ActAsync(head, question.Id, QuestionAction.Reject, "Correct the reasoning");
        Assert.Equal("Rejected", question.Status); Assert.Equal(head, question.ReviewedBy!.Id);
        await using (var db = fixture.CreateContext()) question = await Service(db).UpdateAsync(owner, question.Id, Input("Corrected"), default);
        Assert.Equal("Rejected", question.Status); Assert.Equal(1, question.Revision);
        question = await ActAsync(owner, question.Id, QuestionAction.Submit);
        Assert.Null(question.ReviewedBy); Assert.Null(question.ReviewNote);
        question = await ActAsync(head, question.Id, QuestionAction.Approve, "Ready");
        Assert.Equal("Approved", question.Status);
        await using (var db = fixture.CreateContext()) question = await Service(db).UpdateAsync(owner, question.Id, Input("Corrected"), default);
        Assert.Equal("Approved", question.Status); Assert.Equal(1, question.Revision);
        await using (var db = fixture.CreateContext()) question = await Service(db).UpdateAsync(owner, question.Id, Input("New edition"), default);
        Assert.Equal("Draft", question.Status); Assert.Equal(2, question.Revision); Assert.Null(question.ReviewedBy);
        await using var verify = fixture.CreateContext();
        var audit = await verify.AuditLogs.Where(x => x.EntityId == question.Id).ToListAsync();
        Assert.Equal(7, audit.Count);
        Assert.Contains(audit, x => x.FromStatus == "Submitted" && x.ToStatus == "Rejected" && x.ActorId == head);
        Assert.Contains(audit, x => x.FromStatus == "Approved" && x.ToStatus == "Draft" && x.Note == "revision=1->2");
    }

    [PostgresFact]
    // Enforce creator ownership, department scope, forbidden admin authorship and admin self-review after a role change.
    public async Task Permissions_are_rechecked_from_database_for_every_mutation()
    {
        await SeedAsync();
        var question = await CreateAsync(head);
        await ActAsync(head, question.Id, QuestionAction.Submit);
        Assert.Equal("SelfReview", (await Assert.ThrowsAsync<QuestionException>(() => ActAsync(head, question.Id, QuestionAction.Approve))).Code);
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<QuestionException>(() => ActAsync(outsider, question.Id, QuestionAction.Approve))).Code);
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<QuestionException>(() => ActAsync(owner, question.Id, QuestionAction.Withdraw))).Code);
        await using (var db = fixture.CreateContext())
        {
            Assert.Equal(403, (await Assert.ThrowsAsync<QuestionException>(() => Service(db).CreateAsync(admin, Input(), default))).StatusCode);
            await db.Users.Where(x => x.Id == head).ExecuteUpdateAsync(x => x.SetProperty(u => u.Role, "admin"));
        }
        Assert.Equal("SelfReview", (await Assert.ThrowsAsync<QuestionException>(() => ActAsync(head, question.Id, QuestionAction.Approve))).Code);
        await using (var db = fixture.CreateContext()) await db.Users.Where(x => x.Id == otherHead).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        Assert.Equal("Unauthorized", (await Assert.ThrowsAsync<QuestionException>(() => ActAsync(otherHead, question.Id, QuestionAction.Approve))).Code);
        Assert.Equal("Approved", (await ActAsync(admin, question.Id, QuestionAction.Approve)).Status);
    }

    [PostgresFact]
    // Submitted questions are immutable, and archived questions cannot be edited, deleted or restored through workflow actions.
    public async Task Submitted_and_archived_questions_cannot_be_modified_outside_their_workflow()
    {
        await SeedAsync();
        var question = await CreateAsync();
        await ActAsync(owner, question.Id, QuestionAction.Submit);
        await using (var db = fixture.CreateContext())
            Assert.Equal("InvalidStateTransition", (await Assert.ThrowsAsync<QuestionException>(() => Service(db).UpdateAsync(owner, question.Id, Input("Blocked"), default))).Code);
        await ActAsync(owner, question.Id, QuestionAction.Withdraw);
        await ActAsync(owner, question.Id, QuestionAction.Submit);
        await ActAsync(head, question.Id, QuestionAction.Approve);
        question = await ActAsync(head, question.Id, QuestionAction.Archive);
        Assert.Equal("Archived", question.Status);
        foreach (var action in new[] { QuestionAction.Submit, QuestionAction.Withdraw, QuestionAction.Archive })
            Assert.Equal("InvalidStateTransition", (await Assert.ThrowsAsync<QuestionException>(() => ActAsync(owner, question.Id, action))).Code);
        await using var db2 = fixture.CreateContext();
        Assert.Equal("InvalidStateTransition", (await Assert.ThrowsAsync<QuestionException>(() => Service(db2).DeleteAsync(owner, question.Id, default))).Code);
    }

    [PostgresFact]
    // Review queues omit self-authored work, honor live departments and keep oldest submissions first.
    public async Task Lists_and_review_queue_apply_visibility_filters_and_pagination_in_SQL()
    {
        await SeedAsync();
        var ownDraft = await CreateAsync();
        var headDraft = await CreateAsync(head);
        var submitted = await CreateAsync();
        await ActAsync(owner, submitted.Id, QuestionAction.Submit);
        var headSubmitted = await CreateAsync(head);
        await ActAsync(head, headSubmitted.Id, QuestionAction.Submit);
        await using var db = fixture.CreateContext();
        var service = Service(db);
        Assert.Equal(2, (await service.ListAsync(owner, new(), default)).TotalItems);
        Assert.Equal(2, (await service.ListAsync(head, new(), default)).TotalItems);
        Assert.Equal(2, (await service.ListAsync(owner, new(Mine: true), default)).TotalItems);
        Assert.Equal(404, (await Assert.ThrowsAsync<QuestionException>(() => service.GetAsync(owner, headDraft.Id, default))).StatusCode);
        Assert.Equal(4, (await service.ListAsync(admin, new(), default)).TotalItems);
        Assert.Equal(submitted.Id, Assert.Single((await service.ReviewQueueAsync(head, new(), default)).Items).Id);
        Assert.Equal(2, (await service.ReviewQueueAsync(admin, new(), default)).TotalItems);
        Assert.Empty((await service.ReviewQueueAsync(outsider, new(), default)).Items);
        Assert.Equal(403, (await Assert.ThrowsAsync<QuestionException>(() => service.ReviewQueueAsync(owner, new(), default))).StatusCode);
        Assert.Empty((await service.ListAsync(owner, new(Page: 2, PageSize: 100), default)).Items);
        Assert.Equal(400, (await Assert.ThrowsAsync<QuestionException>(() => service.ListAsync(owner, new(Status: "Unknown"), default))).StatusCode);
    }

    [PostgresFact]
    // The worker's AI provenance survives edits while review and verification flags are invalidated.
    public async Task Ai_questions_require_author_review_and_reset_flags_after_content_changes()
    {
        await SeedAsync();
        var q = await CreateAsync();
        await using (var db = fixture.CreateContext())
        {
            var job = new AiGenerationJob { Id = Guid.NewGuid(), RequestedBy = owner };
            db.AiGenerationJobs.Add(job); await db.SaveChangesAsync();
            await db.Questions.Where(x => x.Id == q.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Source, "AI")
                .SetProperty(v => v.AiJobId, job.Id).SetProperty(v => v.AiOrdinal, 1).SetProperty(v => v.AiVerification, "Agree"));
        }
        Assert.Equal("AiReviewRequired", (await Assert.ThrowsAsync<QuestionException>(() => ActAsync(owner, q.Id, QuestionAction.Submit))).Code);
        await ActAsync(owner, q.Id, QuestionAction.MarkReviewed);
        await ActAsync(owner, q.Id, QuestionAction.Submit);
        await ActAsync(owner, q.Id, QuestionAction.Withdraw);
        await using var edit = fixture.CreateContext();
        q = await Service(edit).UpdateAsync(owner, q.Id, Input("Changed AI content"), default);
        Assert.False(q.AiReviewedByAuthor); Assert.Equal("NotChecked", q.AiVerification); Assert.NotNull(q.AiJobId); Assert.Equal(1, q.AiOrdinal);
    }

    [PostgresFact]
    // Exam links block deletion; collection membership is removed atomically with the eligible source question.
    public async Task Deletion_preserves_exam_references_but_unlinks_collection_membership()
    {
        await SeedAsync();
        var q = await CreateAsync();
        await using var db = fixture.CreateContext();
        var exam = new Exam { Id = Guid.NewGuid(), Title = "Synthetic", SubjectId = Subject, DepartmentId = Department, Grade = 10, CreatedBy = owner };
        var version = new ExamVersion { Id = Guid.NewGuid(), Exam = exam, VersionNo = 1, DurationMinutes = 45, TotalPoints = 10 };
        db.ExamVersions.Add(version);
        await db.SaveChangesAsync();
        db.ExamVersionQuestions.Add(new ExamVersionQuestion { ExamVersionId = version.Id, SourceQuestionId = q.Id, SourceRevision = 1, SortOrder = 1, Points = 10 });
        await db.SaveChangesAsync();
        Assert.Equal("QuestionReferenced", (await Assert.ThrowsAsync<QuestionException>(() => Service(db).DeleteAsync(owner, q.Id, default))).Code);
        await db.ExamVersionQuestions.Where(x => x.SourceQuestionId == q.Id).ExecuteDeleteAsync();
        var set = new QuestionSet { Id = Guid.NewGuid(), Name = "Synthetic", CreatedBy = head };
        db.QuestionSets.Add(set); db.QuestionSetItems.Add(new QuestionSetItem { QuestionSetId = set.Id, QuestionId = q.Id });
        await db.SaveChangesAsync();
        await Service(db).DeleteAsync(owner, q.Id, default);
        Assert.False(await db.QuestionSetItems.AnyAsync(x => x.QuestionId == q.Id));
        Assert.True(await db.QuestionSets.AnyAsync(x => x.Id == set.Id && x.CreatedBy == head));
        Assert.False(await db.Questions.AnyAsync(x => x.Id == q.Id));
        Assert.True(await db.AuditLogs.AnyAsync(x => x.EntityId == q.Id && x.Action == "DeleteQuestion"));
    }

    [PostgresFact]
    // Submitted references block reopening; finalized snapshots remain unchanged when editing is allowed again.
    public async Task Submitted_exams_preserve_sources_until_the_version_is_finalized()
    {
        await SeedAsync();
        var q = await CreateAsync(); await ActAsync(owner, q.Id, QuestionAction.Submit); await ActAsync(head, q.Id, QuestionAction.Approve);
        var versionId = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            var exam = new Exam { Id = Guid.NewGuid(), Title = "Synthetic", SubjectId = Subject, DepartmentId = Department, Grade = 10, CreatedBy = owner };
            db.ExamVersions.Add(new ExamVersion { Id = versionId, Exam = exam, VersionNo = 1, DurationMinutes = 45, TotalPoints = 10, ContentHash = new string('a', 64) });
            await db.SaveChangesAsync();
            db.ExamVersionQuestions.Add(new ExamVersionQuestion
            {
                ExamVersionId = versionId,
                SourceQuestionId = q.Id,
                SourceRevision = 1,
                SortOrder = 1,
                Points = 10,
                Type = q.Type,
                Difficulty = q.Difficulty,
                StemSnapshot = q.Stem,
                PayloadSnapshot = Payload
            });
            await db.SaveChangesAsync();
            await db.ExamVersions.Where(x => x.Id == versionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "Submitted").SetProperty(v => v.SubmittedAt, DateTime.UtcNow));
        }
        await using var edit = fixture.CreateContext();
        Assert.Equal("QuestionReferenced", (await Assert.ThrowsAsync<QuestionException>(() => Service(edit).UpdateAsync(owner, q.Id, Input("New source"), default))).Code);
        var snapshot = await edit.ExamVersionQuestions.SingleAsync(x => x.ExamVersionId == versionId);
        Assert.Equal(q.Stem, snapshot.StemSnapshot); Assert.Equal(1, snapshot.SourceRevision);
        var movedInput = Input("New source") with { TopicId = Guid.Parse("30000000-0000-0000-0000-000000000011"), Grade = 11 };
        Assert.Equal("QuestionReferenced", (await Assert.ThrowsAsync<QuestionException>(() => Service(edit).UpdateAsync(owner, q.Id, movedInput, default))).Code);
        await edit.ExamVersions.Where(x => x.Id == versionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "Approved")
            .SetProperty(v => v.ReviewedBy, head).SetProperty(v => v.ReviewedAt, DateTime.UtcNow));
        var reclassified = await Service(edit).UpdateAsync(owner, q.Id, movedInput, default);
        Assert.Equal(11, reclassified.Grade); Assert.Equal(2, reclassified.Revision);
        Assert.Equal(q.Stem, (await edit.ExamVersionQuestions.AsNoTracking().SingleAsync(x => x.ExamVersionId == versionId)).StemSnapshot);
    }

    [PostgresFact]
    // Exactly one decision wins; the loser observes either an approved-state conflict or a now-private rejected question.
    public async Task Concurrent_review_decisions_do_not_overwrite_each_other()
    {
        await SeedAsync();
        var q = await CreateAsync(); await ActAsync(owner, q.Id, QuestionAction.Submit);
        async Task<string> Decide(Guid actor, QuestionAction action)
        {
            try { return (await ActAsync(actor, q.Id, action, "Synthetic decision")).Status; }
            catch (QuestionException error) { return error.Code; }
        }
        var outcomes = await Task.WhenAll(Decide(head, QuestionAction.Approve), Decide(otherHead, QuestionAction.Reject));
        var winner = Assert.Single(outcomes, x => x is "Approved" or "Rejected");
        var loser = Assert.Single(outcomes, x => x != winner);
        Assert.Equal(winner == "Rejected" ? "NotFound" : "InvalidStateTransition", loser);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.AuditLogs.CountAsync(x => x.EntityId == q.Id && (x.Action == "ApproveQuestion" || x.Action == "RejectQuestion")));
    }

    [PostgresFact]
    // Force both decision orders so scheduling cannot hide the rejected-question privacy branch.
    public async Task Later_review_decisions_respect_the_winning_state_and_visibility()
    {
        await SeedAsync();
        foreach (var first in new[] { QuestionAction.Approve, QuestionAction.Reject })
        {
            var q = await CreateAsync();
            await ActAsync(owner, q.Id, QuestionAction.Submit);
            var winner = await ActAsync(head, q.Id, first, "First decision");
            var second = first == QuestionAction.Approve ? QuestionAction.Reject : QuestionAction.Approve;
            var error = await Assert.ThrowsAsync<QuestionException>(() => ActAsync(otherHead, q.Id, second, "Late decision"));
            Assert.Equal(first == QuestionAction.Reject ? "NotFound" : "InvalidStateTransition", error.Code);
            Assert.Equal(first == QuestionAction.Reject ? 404 : 409, error.StatusCode);
            await using var verify = fixture.CreateContext();
            Assert.Equal(winner.Status, (await verify.Questions.SingleAsync(x => x.Id == q.Id)).Status);
            Assert.Equal(1, await verify.AuditLogs.CountAsync(x => x.EntityId == q.Id
                && (x.Action == "ApproveQuestion" || x.Action == "RejectQuestion")));
        }
    }

    [PostgresFact]
    // Failure to persist the audit must roll back both the row update and its status metadata.
    public async Task Audit_failure_rolls_back_the_question_transition()
    {
        await SeedAsync(); var q = await CreateAsync();
        var options = new DbContextOptionsBuilder<ExamMakerDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(new FailAudit()).Options;
        await using (var db = new ExamMakerDbContext(options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).TransitionAsync(owner, q.Id, QuestionAction.Submit, null, default));
        await using (var db = new ExamMakerDbContext(options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).DeleteAsync(owner, q.Id, default));
        await using var verify = fixture.CreateContext();
        Assert.Equal("Draft", (await verify.Questions.SingleAsync(x => x.Id == q.Id)).Status);
        Assert.Equal(1, await verify.AuditLogs.CountAsync(x => x.EntityId == q.Id));
    }

    [PostgresFact]
    // Catalog membership and fixed department are server-derived; malformed content cannot be stored or audited as success.
    public async Task Catalog_and_payload_validation_prevent_cross_department_content_moves()
    {
        await SeedAsync(); var q = await CreateAsync();
        await using var db = fixture.CreateContext();
        var other = await db.Topics.FirstAsync(x => x.SubjectId != Subject && x.Grade == 10);
        var outside = Input() with { SubjectId = other.SubjectId, TopicId = other.Id };
        Assert.Equal(403, (await Assert.ThrowsAsync<QuestionException>(() => Service(db).CreateAsync(owner, outside, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<QuestionException>(() => Service(db).UpdateAsync(owner, q.Id, outside, default))).StatusCode);
        var bad = Input() with { Payload = JsonSerializer.Deserialize<JsonElement>("{}") };
        Assert.Equal("payload", (await Assert.ThrowsAsync<QuestionException>(() => Service(db).CreateAsync(owner, bad, default))).Field);
        Assert.Equal(400, (await Assert.ThrowsAsync<QuestionException>(() => Service(db).UpdateAsync(owner, q.Id,
            Input() with { Grade = 11 }, default))).StatusCode);
        Assert.Equal(Department, (await Service(db).GetAsync(owner, q.Id, default)).DepartmentId);
        Assert.Equal(1, await db.AuditLogs.CountAsync());
    }

    [PostgresFact]
    // Observe a real lock waiter before committing a deactivation, so an authorization check moved before the lock fails this test.
    public async Task Activation_is_rechecked_after_waiting_for_the_actor_row_lock()
    {
        await SeedAsync(); var q = await CreateAsync();
        await using var holder = fixture.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Users.Where(x => x.Id == owner).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        await using var attempt = fixture.CreateContext();
        await attempt.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)attempt.Database.GetDbConnection()).ProcessID;
        var holderPid = ((Npgsql.NpgsqlConnection)holder.Database.GetDbConnection()).ProcessID;
        var pending = Service(attempt).UpdateAsync(owner, q.Id, Input("Should not be saved"), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.8));
        while (!await holder.Database.SqlQuery<bool>($"""
            SELECT {holderPid} = ANY(pg_blocking_pids({pid})) AS "Value"
            """).SingleAsync(timeout.Token)) await Task.Delay(10, timeout.Token);
        await transaction.CommitAsync();
        var error = await Assert.ThrowsAsync<QuestionException>(() => pending);
        Assert.Equal(401, error.StatusCode); Assert.Equal("Unauthorized", error.Code);
        Assert.Equal("Draft", (await attempt.Questions.AsNoTracking().SingleAsync(x => x.Id == q.Id)).Status);
        Assert.Equal(q.Stem, (await attempt.Questions.AsNoTracking().SingleAsync(x => x.Id == q.Id)).Stem);
    }


    [PostgresFact]
    // An unrelated A4 transaction may retain its global lock and another user row without stalling question writes.
    public async Task Question_writes_do_not_wait_for_unrelated_user_management()
    {
        await SeedAsync();
        await using var holder = fixture.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(48683401)");
        await holder.Users.Where(x => x.Id == otherHead).ExecuteUpdateAsync(x => x.SetProperty(u => u.FullName, "Uncommitted profile"));
        var question = await CreateAsync();
        Assert.Equal("Draft", question.Status);
        Assert.NotEqual(Guid.Empty, question.Id);
    }

    [PostgresFact]
    // Every mutation must hide private IDs exactly like GET, before role and state checks disclose existence.
    public async Task Private_question_identifiers_are_hidden_from_all_mutation_endpoints()
    {
        await SeedAsync(); var q = await CreateAsync();
        await using var db = fixture.CreateContext();
        var service = Service(db);
        foreach (var actor in new[] { head, outsider })
        {
            Assert.Equal(404, (await Assert.ThrowsAsync<QuestionException>(() => service.GetAsync(actor, q.Id, default))).StatusCode);
            Assert.Equal(404, (await Assert.ThrowsAsync<QuestionException>(() => service.UpdateAsync(actor, q.Id, Input(), default))).StatusCode);
            Assert.Equal(404, (await Assert.ThrowsAsync<QuestionException>(() => service.DeleteAsync(actor, q.Id, default))).StatusCode);
            foreach (var action in Enum.GetValues<QuestionAction>())
                Assert.Equal(404, (await Assert.ThrowsAsync<QuestionException>(() => service.TransitionAsync(actor, q.Id, action, "Reason", default))).StatusCode);
        }
    }

    [PostgresFact]
    // A workflow lock must be reported before a mismatched catalog leads the client toward editing the wrong field.
    public async Task Locked_question_edits_return_state_conflicts_before_catalog_errors()
    {
        await SeedAsync(); var q = await CreateAsync();
        await ActAsync(owner, q.Id, QuestionAction.Submit);
        var wrong = Input() with { SubjectId = Guid.Parse("20000000-0000-0000-0000-000000000002") };
        await using (var db = fixture.CreateContext())
            Assert.Equal("InvalidStateTransition", (await Assert.ThrowsAsync<QuestionException>(() => Service(db).UpdateAsync(owner, q.Id, wrong, default))).Code);
        await ActAsync(head, q.Id, QuestionAction.Approve); await ActAsync(owner, q.Id, QuestionAction.Archive);
        await using var archived = fixture.CreateContext();
        Assert.Equal("InvalidStateTransition", (await Assert.ThrowsAsync<QuestionException>(() => Service(archived).UpdateAsync(owner, q.Id, wrong, default))).Code);
    }

    [PostgresFact]
    // Classification changes must not silently invalidate editable exam membership, regardless of the source question state.
    public async Task Editable_exam_references_block_subject_topic_and_grade_changes()
    {
        await SeedAsync();
        await using var db = fixture.CreateContext();
        var secondSubject = new Subject { Id = Guid.NewGuid(), DepartmentId = Department, Name = "Synthetic alternative subject" };
        var secondTopic = new Topic { Id = Guid.NewGuid(), Subject = secondSubject, Grade = 10, Name = "Synthetic chapter", SortOrder = 1 };
        db.Topics.Add(secondTopic); await db.SaveChangesAsync();
        foreach (var state in new[] { "Draft", "Rejected", "Approved" })
        {
            var q = await CreateAsync();
            if (state != "Draft")
            {
                await ActAsync(owner, q.Id, QuestionAction.Submit);
                await ActAsync(head, q.Id, state == "Rejected" ? QuestionAction.Reject : QuestionAction.Approve, "Reason");
            }
            foreach (var versionState in new[] { "Draft", "Rejected" })
            {
                var exam = new Exam { Id = Guid.NewGuid(), Title = "Synthetic", SubjectId = Subject, DepartmentId = Department, Grade = 10, CreatedBy = owner };
                var version = new ExamVersion
                {
                    Id = Guid.NewGuid(),
                    Exam = exam,
                    VersionNo = 1,
                    DurationMinutes = 45,
                    TotalPoints = 10,
                    Status = versionState,
                    ReviewNote = versionState == "Rejected" ? "Reason" : null,
                    ReviewedBy = versionState == "Rejected" ? head : null,
                    ReviewedAt = versionState == "Rejected" ? DateTime.UtcNow : null
                };
                db.ExamVersions.Add(version); await db.SaveChangesAsync();
                db.ExamVersionQuestions.Add(new ExamVersionQuestion { ExamVersionId = version.Id, SourceQuestionId = q.Id, SourceRevision = 1, SortOrder = 1, Points = 10 });
                await db.SaveChangesAsync();
                foreach (var input in new[]
                {
                    Input() with { TopicId = Guid.Parse("30000000-0000-0000-0000-000000000002") },
                    Input() with { TopicId = Guid.Parse("30000000-0000-0000-0000-000000000011"), Grade = 11 },
                    Input() with { SubjectId = secondSubject.Id, TopicId = secondTopic.Id }
                })
                {
                    await using var attempt = fixture.CreateContext();
                    Assert.Equal("QuestionReferenced", (await Assert.ThrowsAsync<QuestionException>(() => Service(attempt).UpdateAsync(owner, q.Id, input, default))).Code);
                }
                await db.ExamVersionQuestions.Where(x => x.ExamVersionId == version.Id).ExecuteDeleteAsync();
            }
        }
    }

    [PostgresFact]
    // Stored AI output using the documented comma-decimal example must survive revalidation on submit.
    public async Task Ai_numeric_short_answers_with_decimal_commas_can_be_submitted()
    {
        await SeedAsync();
        await using var db = fixture.CreateContext();
        var input = Input() with { Type = "ShortAnswer", Payload = JsonSerializer.Deserialize<JsonElement>("""{"answerType":"number","acceptedAnswers":["12","12,0"]}""") };
        var q = await Service(db).CreateAsync(owner, input, default);
        var job = new AiGenerationJob { Id = Guid.NewGuid(), RequestedBy = owner };
        db.AiGenerationJobs.Add(job); await db.SaveChangesAsync();
        await db.Questions.Where(x => x.Id == q.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Source, "AI")
            .SetProperty(v => v.AiJobId, job.Id).SetProperty(v => v.AiOrdinal, 1).SetProperty(v => v.AiReviewedByAuthor, true));
        q = await ActAsync(owner, q.Id, QuestionAction.Submit);
        Assert.Equal("Submitted", q.Status); Assert.Equal("12,0", q.Payload.GetProperty("acceptedAnswers")[1].GetString());
    }

    [PostgresFact]
    // Audit notes belong only to the reviewer decision, never to a later archive or author-review action.
    public async Task Transition_audits_do_not_attribute_old_review_notes_to_other_actions()
    {
        await SeedAsync(); var q = await CreateAsync();
        await using (var db = fixture.CreateContext())
        {
            var job = new AiGenerationJob { Id = Guid.NewGuid(), RequestedBy = owner };
            db.AiGenerationJobs.Add(job); await db.SaveChangesAsync();
            await db.Questions.Where(x => x.Id == q.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Source, "AI")
                .SetProperty(v => v.AiJobId, job.Id).SetProperty(v => v.AiOrdinal, 1));
        }
        await ActAsync(owner, q.Id, QuestionAction.MarkReviewed); await ActAsync(owner, q.Id, QuestionAction.Submit);
        await ActAsync(head, q.Id, QuestionAction.Reject, "Old rejection");
        await ActAsync(owner, q.Id, QuestionAction.MarkReviewed); await ActAsync(owner, q.Id, QuestionAction.Submit);
        await ActAsync(head, q.Id, QuestionAction.Approve, "OK"); await ActAsync(head, q.Id, QuestionAction.Archive);
        await using var verify = fixture.CreateContext();
        var audit = await verify.AuditLogs.Where(x => x.EntityId == q.Id).ToListAsync();
        Assert.Equal("OK", audit.Single(x => x.Action == "ApproveQuestion").Note);
        Assert.Equal("Old rejection", audit.Single(x => x.Action == "RejectQuestion").Note);
        Assert.Null(audit.Single(x => x.Action == "ArchiveQuestion").Note);
        Assert.All(audit.Where(x => x.Action == "MarkReviewedQuestion" || x.Action == "SubmitQuestion"), x => Assert.Null(x.Note));
    }

    [PostgresFact]
    // An author's eligible source deletion must not depend on permission to edit another teacher's collection.
    public async Task Deleting_reopened_or_rejected_questions_unlinks_other_users_collections()
    {
        await SeedAsync();
        foreach (var reject in new[] { false, true })
        {
            var q = await CreateAsync(); await ActAsync(owner, q.Id, QuestionAction.Submit); await ActAsync(head, q.Id, QuestionAction.Approve);
            var setId = Guid.NewGuid(); var secondSetId = Guid.NewGuid();
            await using (var db = fixture.CreateContext())
            {
                db.QuestionSets.Add(new QuestionSet { Id = setId, Name = "Other user's collection", CreatedBy = otherHead });
                db.QuestionSetItems.Add(new QuestionSetItem { QuestionSetId = setId, QuestionId = q.Id });
                db.QuestionSets.Add(new QuestionSet { Id = secondSetId, Name = "Another collection", CreatedBy = head });
                db.QuestionSetItems.Add(new QuestionSetItem { QuestionSetId = secondSetId, QuestionId = q.Id });
                await db.SaveChangesAsync();
                q = await Service(db).UpdateAsync(owner, q.Id, Input("Reopened"), default);
                Assert.Equal(2, q.Revision);
            }
            if (reject) { await ActAsync(owner, q.Id, QuestionAction.Submit); await ActAsync(head, q.Id, QuestionAction.Reject, "Reason"); }
            await using var deletion = fixture.CreateContext();
            await Service(deletion).DeleteAsync(owner, q.Id, default);
            Assert.False(await deletion.Questions.AnyAsync(x => x.Id == q.Id));
            Assert.False(await deletion.QuestionSetItems.AnyAsync(x => x.QuestionId == q.Id));
            Assert.True(await deletion.QuestionSets.AnyAsync(x => x.Id == setId && x.CreatedBy == otherHead));
            Assert.Contains("removedCollectionLinks=2", (await deletion.AuditLogs.SingleAsync(x => x.EntityId == q.Id && x.Action == "DeleteQuestion")).Note);
            var setAudits = await deletion.AuditLogs.Where(x => x.EntityType == "QuestionSet"
                && (x.EntityId == setId || x.EntityId == secondSetId)).ToListAsync();
            Assert.Equal(2, setAudits.Count);
            Assert.Equal(2, setAudits.Select(x => x.EntityId).Distinct().Count());
            Assert.All(setAudits, audit =>
            {
                Assert.Equal(owner, audit.ActorId);
                Assert.Equal("RemoveQuestionFromSet", audit.Action);
                Assert.Contains($"question={q.Id}", audit.Note);
            });
        }
    }

    [PostgresFact]
    // Unlinking collections must roll back when the enclosing question deletion/audit does not commit.
    public async Task Failed_deletion_restores_collection_membership_and_the_question()
    {
        await SeedAsync(); var q = await CreateAsync(); var setId = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            db.QuestionSets.Add(new QuestionSet { Id = setId, Name = "Other user's collection", CreatedBy = head });
            db.QuestionSetItems.Add(new QuestionSetItem { QuestionSetId = setId, QuestionId = q.Id });
            await db.SaveChangesAsync();
        }
        var options = new DbContextOptionsBuilder<ExamMakerDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(new FailAudit()).Options;
        await using (var db = new ExamMakerDbContext(options))
            await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).DeleteAsync(owner, q.Id, default));
        await using var verify = fixture.CreateContext();
        Assert.True(await verify.Questions.AnyAsync(x => x.Id == q.Id));
        Assert.True(await verify.QuestionSetItems.AnyAsync(x => x.QuestionSetId == setId && x.QuestionId == q.Id));
        Assert.False(await verify.AuditLogs.AnyAsync(x => x.EntityId == q.Id && x.Action == "DeleteQuestion"));
        Assert.False(await verify.AuditLogs.AnyAsync(x => x.EntityType == "QuestionSet" && x.EntityId == setId));
    }

    [PostgresFact]
    // An account removed after token validation has no usable session, regardless of the requested question identifier.
    public async Task Missing_live_accounts_return_unauthorized_for_reads_and_mutations()
    {
        await SeedAsync(); var q = await CreateAsync(); var missingActor = Guid.NewGuid();
        await using var db = fixture.CreateContext();
        var read = await Assert.ThrowsAsync<QuestionException>(() => Service(db).GetAsync(missingActor, q.Id, default));
        Assert.Equal(401, read.StatusCode); Assert.Equal("Unauthorized", read.Code);
        var write = await Assert.ThrowsAsync<QuestionException>(() => Service(db).UpdateAsync(missingActor, q.Id, Input(), default));
        Assert.Equal(401, write.StatusCode); Assert.Equal("Unauthorized", write.Code);
    }

    [PostgresFact]
    // Repeated author-review requests remain successful without duplicating audit history or modifying timestamps.
    public async Task Retrying_mark_reviewed_keeps_one_audit_and_the_first_updated_at()
    {
        await SeedAsync(); var q = await CreateAsync();
        await using (var db = fixture.CreateContext())
        {
            var job = new AiGenerationJob { Id = Guid.NewGuid(), RequestedBy = owner };
            db.AiGenerationJobs.Add(job); await db.SaveChangesAsync();
            await db.Questions.Where(x => x.Id == q.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Source, "AI")
                .SetProperty(v => v.AiJobId, job.Id).SetProperty(v => v.AiOrdinal, 1));
        }
        var first = await ActAsync(owner, q.Id, QuestionAction.MarkReviewed);
        for (var retry = 0; retry < 3; retry++)
        {
            var result = await ActAsync(owner, q.Id, QuestionAction.MarkReviewed);
            Assert.True(result.AiReviewedByAuthor); Assert.Equal(first.UpdatedAt, result.UpdatedAt);
        }
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.AuditLogs.CountAsync(x => x.EntityId == q.Id && x.Action == "MarkReviewedQuestion"));
    }
    [PostgresFact]
    // Reopening approved content must not silently remove a usable question from somebody else's editable exam.
    public async Task Approved_content_edits_preserve_other_authors_editable_exam_sources()
    {
        await SeedAsync();
        foreach (var state in new[] { "Draft", "Rejected", "Submitted" })
        {
            var q = await CreateAsync(); await ActAsync(owner, q.Id, QuestionAction.Submit);
            q = await ActAsync(head, q.Id, QuestionAction.Approve);
            var version = new ExamVersion
            {
                Id = Guid.NewGuid(),
                Exam = new Exam
                {
                    Id = Guid.NewGuid(),
                    Title = "Another author's exam",
                    SubjectId = Subject,
                    DepartmentId = Department,
                    Grade = 10,
                    CreatedBy = otherHead
                },
                VersionNo = 1,
                DurationMinutes = 45,
                TotalPoints = 10,
                Status = state == "Submitted" ? "Draft" : state,
                ContentHash = state == "Submitted" ? new string('a', 64) : null,
                ReviewNote = state == "Rejected" ? "Reason" : null,
                ReviewedBy = state == "Rejected" ? head : null,
                ReviewedAt = state == "Rejected" ? DateTime.UtcNow : null
            };
            await using (var setup = fixture.CreateContext())
            {
                setup.ExamVersions.Add(version); await setup.SaveChangesAsync();
                setup.ExamVersionQuestions.Add(new ExamVersionQuestion
                {
                    ExamVersionId = version.Id,
                    SourceQuestionId = q.Id,
                    SourceRevision = 1,
                    SortOrder = 1,
                    Points = 10,
                    Type = q.Type,
                    Difficulty = q.Difficulty,
                    StemSnapshot = q.Stem,
                    PayloadSnapshot = Payload
                });
                await setup.SaveChangesAsync();
                if (state == "Submitted")
                    await setup.ExamVersions.Where(x => x.Id == version.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "Submitted")
                        .SetProperty(v => v.SubmittedAt, DateTime.UtcNow));
            }
            await using (var noop = fixture.CreateContext())
            {
                var unchanged = await Service(noop).UpdateAsync(owner, q.Id, Input(" Synthetic question "), default);
                Assert.Equal("Approved", unchanged.Status); Assert.Equal(q.UpdatedAt, unchanged.UpdatedAt);
            }
            foreach (var input in new[] { Input("Changed stem"), Input() with { Difficulty = "Application" },
                Input() with { Explanation = "Changed explanation" },
                Input() with { Payload = JsonSerializer.Deserialize<JsonElement>(Payload.Replace("One", "Changed option")) } })
            {
                await using var attempt = fixture.CreateContext();
                var error = await Assert.ThrowsAsync<QuestionException>(() => Service(attempt).UpdateAsync(owner, q.Id, input, default));
                Assert.Equal(409, error.StatusCode); Assert.Equal("QuestionReferenced", error.Code);
            }
            await using var verify = fixture.CreateContext();
            var visible = await Service(verify).GetAsync(otherHead, q.Id, default);
            foreach (var actor in new[] { owner, head, admin })
            {
                var error = await Assert.ThrowsAsync<QuestionException>(() => ActAsync(actor, q.Id, QuestionAction.Archive));
                Assert.Equal(409, error.StatusCode); Assert.Equal("QuestionReferenced", error.Code);
                Assert.Equal(1, error.References!.TotalItems);
                var reference = Assert.Single(error.References.Items);
                Assert.Equal(version.Id, reference.VersionId); Assert.Equal(state, reference.Status);
                Assert.Equal(otherHead, reference.CreatedBy.Id); Assert.Equal("Synthetic head", reference.CreatedBy.FullName);
            }
            Assert.Equal("Approved", visible.Status); Assert.Equal(1, visible.Revision); Assert.Equal(q.Stem, visible.Stem);
            Assert.Equal(3, await verify.AuditLogs.CountAsync(x => x.EntityId == q.Id));
            if (state == "Submitted")
                await verify.ExamVersions.Where(x => x.Id == version.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "Draft"));
            await verify.ExamVersionQuestions.Where(x => x.ExamVersionId == version.Id).ExecuteDeleteAsync();
            Assert.Equal("Draft", (await Service(verify).UpdateAsync(owner, q.Id, Input("Now editable"), default)).Status);
        }
    }

    [PostgresFact]
    // Finalized versions use immutable snapshots and must not permanently prevent retiring their source questions.
    public async Task Finalized_exam_snapshots_survive_source_archival()
    {
        await SeedAsync();
        foreach (var state in new[] { "Approved", "Published", "Archived" })
        {
            var q = await CreateAsync(); await ActAsync(owner, q.Id, QuestionAction.Submit);
            q = await ActAsync(head, q.Id, QuestionAction.Approve);
            await using var db = fixture.CreateContext();
            var version = new ExamVersion
            {
                Id = Guid.NewGuid(),
                VersionNo = 1,
                DurationMinutes = 45,
                TotalPoints = 10,
                ContentHash = new string('a', 64),
                Exam = new Exam
                {
                    Id = Guid.NewGuid(),
                    Title = "Synthetic snapshot",
                    SubjectId = Subject,
                    DepartmentId = Department,
                    Grade = 10,
                    CreatedBy = otherHead
                }
            };
            db.ExamVersions.Add(version); await db.SaveChangesAsync();
            db.ExamVersionQuestions.Add(new ExamVersionQuestion
            {
                ExamVersionId = version.Id,
                SourceQuestionId = q.Id,
                SourceRevision = 1,
                SortOrder = 1,
                Points = 10,
                Type = q.Type,
                Difficulty = q.Difficulty,
                StemSnapshot = q.Stem,
                PayloadSnapshot = Payload
            });
            await db.SaveChangesAsync();
            await db.ExamVersions.Where(x => x.Id == version.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "Submitted")
                .SetProperty(v => v.SubmittedAt, DateTime.UtcNow));
            await db.ExamVersions.Where(x => x.Id == version.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "Approved")
                .SetProperty(v => v.ReviewedBy, head).SetProperty(v => v.ReviewedAt, DateTime.UtcNow));
            if (state != "Approved")
                await db.ExamVersions.Where(x => x.Id == version.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, state)
                    .SetProperty(v => v.PublishedBy, state == "Published" ? (Guid?)admin : null)
                    .SetProperty(v => v.PublishedAt, state == "Published" ? (DateTime?)DateTime.UtcNow : null));
            Assert.Equal("Archived", (await ActAsync(owner, q.Id, QuestionAction.Archive)).Status);
            var snapshot = await db.ExamVersionQuestions.AsNoTracking().SingleAsync(x => x.ExamVersionId == version.Id);
            Assert.Equal(q.Stem, snapshot.StemSnapshot);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(Payload),
                JsonSerializer.Deserialize<JsonElement>(snapshot.PayloadSnapshot!)));
            Assert.Equal(1, snapshot.SourceRevision);
        }
    }

    [PostgresFact]
    // A transferred head retains the same ownership privileges as a teacher for previously authored content.
    public async Task Transferred_heads_can_archive_their_own_approved_questions()
    {
        await SeedAsync(); var q = await CreateAsync(head);
        await ActAsync(head, q.Id, QuestionAction.Submit); await ActAsync(otherHead, q.Id, QuestionAction.Approve);
        await using var db = fixture.CreateContext();
        await db.Users.Where(x => x.Id == head).ExecuteUpdateAsync(x => x.SetProperty(v => v.DepartmentId,
            Guid.Parse("10000000-0000-0000-0000-000000000002")));
        Assert.Equal("Archived", (await ActAsync(head, q.Id, QuestionAction.Archive)).Status);
        Assert.Equal(1, await db.AuditLogs.CountAsync(x => x.EntityId == q.Id && x.Action == "ArchiveQuestion" && x.ActorId == head));
    }

    [PostgresFact]
    // Invalid business payload must not disclose validation detail to a revoked account or an unauthorized creator.
    public async Task Creation_checks_live_actor_before_business_payload_validation()
    {
        await SeedAsync();
        var invalid = Input() with { Payload = JsonSerializer.Deserialize<JsonElement>("{}") };
        await using var db = fixture.CreateContext();
        await db.Users.Where(x => x.Id == owner).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, false));
        foreach (var actor in new[] { owner, Guid.NewGuid() })
        {
            var error = await Assert.ThrowsAsync<QuestionException>(() => Service(db).CreateAsync(actor, invalid, default));
            Assert.Equal(401, error.StatusCode); Assert.Equal("Unauthorized", error.Code);
        }
        Assert.Equal(403, (await Assert.ThrowsAsync<QuestionException>(() => Service(db).CreateAsync(admin, invalid, default))).StatusCode);
        await db.Users.Where(x => x.Id == owner).ExecuteUpdateAsync(x => x.SetProperty(u => u.IsActive, true));
        var validation = await Assert.ThrowsAsync<QuestionException>(() => Service(db).CreateAsync(owner, invalid, default));
        Assert.Equal(400, validation.StatusCode); Assert.Equal("payload", validation.Field);
        Assert.False(await db.Questions.AnyAsync()); Assert.False(await db.AuditLogs.AnyAsync());
    }

    [PostgresFact]
    // A stale review tab cannot approve or reject unseen content after withdrawal, editing and resubmission at the same revision.
    public async Task Review_decisions_require_the_submission_round_that_was_read()
    {
        await SeedAsync();
        foreach (var action in new[] { QuestionAction.Approve, QuestionAction.Reject })
        {
            var q = await CreateAsync(); var first = await ActAsync(owner, q.Id, QuestionAction.Submit);
            await ActAsync(owner, q.Id, QuestionAction.Withdraw);
            await using (var edit = fixture.CreateContext()) await Service(edit).UpdateAsync(owner, q.Id, Input("Unseen replacement"), default);
            var current = await ActAsync(owner, q.Id, QuestionAction.Submit);
            Assert.Equal(first.Revision, current.Revision); Assert.NotEqual(first.SubmittedAt, current.SubmittedAt);
            await using var db = fixture.CreateContext();
            var missing = await Assert.ThrowsAsync<QuestionException>(() => Service(db).TransitionAsync(head, q.Id, action, "Reason", default));
            Assert.Equal(400, missing.StatusCode); Assert.Equal("submittedAt", missing.Field);
            var stale = await Assert.ThrowsAsync<QuestionException>(() => Service(db).TransitionAsync(head, q.Id, action, "Reason", default,
                new DateTimeOffset(first.SubmittedAt!.Value)));
            Assert.Equal(409, stale.StatusCode); Assert.Equal("Conflict", stale.Code);
            Assert.Equal("Submitted", (await db.Questions.AsNoTracking().SingleAsync(x => x.Id == q.Id)).Status);
            Assert.False(await db.AuditLogs.AnyAsync(x => x.EntityId == q.Id && (x.Action == "ApproveQuestion" || x.Action == "RejectQuestion")));
            var result = await Service(db).TransitionAsync(head, q.Id, action, "Reason", default,
                new DateTimeOffset(current.SubmittedAt!.Value).ToOffset(TimeSpan.FromHours(7)));
            Assert.Equal(action == QuestionAction.Approve ? "Approved" : "Rejected", result.Status);
        }
    }

    [PostgresFact]
    // Out-of-range JSON numbers fail safely before jsonb parsing; representable small positive rubric values still persist and submit.
    public async Task Rubric_numeric_limits_return_validation_errors_before_postgres_overflow()
    {
        await SeedAsync();
        foreach (var number in new[] { "1e-20000", "1e200000", "1e-30" })
        {
            var input = Input() with
            {
                Type = "Essay",
                Payload = JsonSerializer.Deserialize<JsonElement>(
                "{\"modelAnswer\":\"\",\"rubric\":[{\"criterion\":\"Synthetic\",\"points\":" + number + "}]}")
            };
            await using var db = fixture.CreateContext();
            if (number != "1e-30")
            {
                var error = await Assert.ThrowsAsync<QuestionException>(() => Service(db).CreateAsync(owner, input, default));
                Assert.Equal(400, error.StatusCode); Assert.Equal("payload", error.Field);
            }
            else
            {
                var created = await Service(db).CreateAsync(owner, input, default);
                Assert.Equal(1e-30, created.Payload.GetProperty("rubric")[0].GetProperty("points").GetDouble());
                Assert.Equal("Submitted", (await ActAsync(owner, created.Id, QuestionAction.Submit)).Status);
            }
        }
    }

    [PostgresFact]
    // Accepted numbers must survive the entire create/read/update/read/submit cycle after jsonb expands scientific notation.
    public async Task Numeric_payloads_round_trip_through_jsonb_before_submission()
    {
        await SeedAsync();
        foreach (var type in new[] { "Essay", "ShortAnswer" })
            foreach (var number in new[] { "1e-1000", "1.2345678901234567890123456789e-990",
            "1.2345678901234567890123456789e-1000", "1e-20000", "1e200000",
            "0." + new string('0', 1021) + "1", "0." + new string('0', 1022) + "1",
            "0." + new string('0', 1020) + "1e1000", "0.0001e1000" })
            {
                var json = type == "Essay"
                    ? "{\"modelAnswer\":\"\",\"rubric\":[{\"criterion\":\"Synthetic\",\"points\":" + number + "}]}"
                    : "{\"answerType\":\"number\",\"acceptedAnswers\":[\"1\"],\"tolerance\":" + number + "}";
                var input = Input() with { Type = type, Payload = JsonSerializer.Deserialize<JsonElement>(json) };
                await using var db = fixture.CreateContext();
                if (number is "1e-20000" or "1e200000" or "1.2345678901234567890123456789e-1000" || number.Length == 1025)
                {
                    var error = await Assert.ThrowsAsync<QuestionException>(() => Service(db).CreateAsync(owner, input, default));
                    Assert.Equal(400, error.StatusCode); Assert.Equal("payload", error.Field);
                    continue;
                }
                var created = await Service(db).CreateAsync(owner, input, default);
                var read = await Service(db).GetAsync(owner, created.Id, default);
                QuestionPayloadRules.Validate(type, read.Payload);
                var edited = await Service(db).UpdateAsync(owner, created.Id, input with { Explanation = "Edited after reading JSONB" }, default);
                QuestionPayloadRules.Validate(type, edited.Payload);
                Assert.Equal("Submitted", (await ActAsync(owner, created.Id, QuestionAction.Submit)).Status);
            }
    }

    [PostgresFact]
    // JavaScript Date drops legacy microseconds; a current-round millisecond token must still permit both decisions.
    public async Task Legacy_microsecond_submissions_accept_javascript_millisecond_round_trips()
    {
        await SeedAsync();
        foreach (var action in new[] { QuestionAction.Approve, QuestionAction.Reject })
        {
            var q = await CreateAsync(); q = await ActAsync(owner, q.Id, QuestionAction.Submit);
            var microseconds = new DateTime(q.SubmittedAt!.Value.Ticks + 4560, DateTimeKind.Utc);
            await using var db = fixture.CreateContext();
            await db.Questions.Where(x => x.Id == q.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.SubmittedAt, microseconds)
                .SetProperty(v => v.UpdatedAt, microseconds));
            var read = await Service(db).GetAsync(head, q.Id, default);
            var javascriptDate = DateTimeOffset.FromUnixTimeMilliseconds(new DateTimeOffset(read.SubmittedAt!.Value).ToUnixTimeMilliseconds());
            Assert.NotEqual(read.SubmittedAt, javascriptDate.UtcDateTime);
            var result = await Service(db).TransitionAsync(head, q.Id, action, "Synthetic decision", default, javascriptDate);
            Assert.Equal(action == QuestionAction.Approve ? "Approved" : "Rejected", result.Status);
        }
    }

    [PostgresFact]
    // Private row existence and lock contention must produce the same immediate 404 as an absent identifier.
    public async Task Invisible_question_probes_do_not_wait_for_or_take_question_row_locks()
    {
        await SeedAsync(); var q = await CreateAsync();
        await using var holder = fixture.CreateContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Questions.FromSqlInterpolated($"SELECT * FROM questions WHERE id = {q.Id} FOR UPDATE").SingleAsync();
        foreach (var actor in new[] { head, outsider })
            foreach (var action in new[] { QuestionAction.Withdraw, QuestionAction.Submit, QuestionAction.Approve, QuestionAction.Archive })
            {
                await using var probe = fixture.CreateContext();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                var error = await Assert.ThrowsAsync<QuestionException>(() => Service(probe).TransitionAsync(actor, q.Id, action, null, deadline.Token));
                Assert.Equal(404, error.StatusCode); Assert.Equal("NotFound", error.Code); Assert.Null(error.References);
            }
    }

    [PostgresFact]
    // Visibility can change after the optimistic read; it must be checked again after the row lock is granted.
    public async Task Question_visibility_is_rechecked_after_a_concurrent_archive_commits()
    {
        await SeedAsync(); var q = await CreateAsync(); await ActAsync(owner, q.Id, QuestionAction.Submit);
        await ActAsync(head, q.Id, QuestionAction.Approve);
        await using var holder = fixture.CreateContext(); await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Questions.Where(x => x.Id == q.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Status, "Archived"));
        await using var attempt = fixture.CreateContext(); await attempt.Database.OpenConnectionAsync();
        var pid = ((Npgsql.NpgsqlConnection)attempt.Database.GetDbConnection()).ProcessID;
        var holderPid = ((Npgsql.NpgsqlConnection)holder.Database.GetDbConnection()).ProcessID;
        var pending = Service(attempt).TransitionAsync(otherHead, q.Id, QuestionAction.Withdraw, null, default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1.8));
        while (!await holder.Database.SqlQuery<bool>($"SELECT {holderPid} = ANY(pg_blocking_pids({pid})) AS \"Value\"").SingleAsync(deadline.Token))
            await Task.Delay(10, deadline.Token);
        await transaction.CommitAsync();
        Assert.Equal(404, (await Assert.ThrowsAsync<QuestionException>(() => pending)).StatusCode);
    }

    private sealed class FailAudit : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData, Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Synthetic audit failure.");
    }
}
