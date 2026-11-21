using System.Text.Json;
using ExamMaker.Application.Exams;
using ExamMaker.Application.Questions;
using ExamMaker.Domain.Questions;
using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Exams;
using ExamMaker.Infrastructure.Persistence;
using ExamMaker.Infrastructure.Questions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ExamMaker.Infrastructure.Tests;

public sealed class ExamManagementTests : IAsyncLifetime
{
    private readonly PostgresFixture fixture = new();
    private readonly Guid owner = Guid.NewGuid(), peer = Guid.NewGuid(), head = Guid.NewGuid(), outsider = Guid.NewGuid(), admin = Guid.NewGuid();
    private static readonly Guid Department = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Subject = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid Topic = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private const string Payload = """{"options":[{"label":"A","text":"One"},{"label":"B","text":"Two"},{"label":"C","text":"Three"},{"label":"D","text":"Four"}],"correctLabel":"A"}""";
    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();
    private static ExamManagement Service(ExamMakerDbContext db) => new(db, NullLogger<ExamManagement>.Instance);

    /// <summary>Use synthetic users and real PostgreSQL constraints without AWS credentials or production data.</summary>
    private async Task SeedAsync()
    {
        await using var db = fixture.CreateContext();
        foreach (var (id, role, department) in new (Guid, string, Guid?)[] { (owner, "teacher", Department), (peer, "teacher", Department),
            (head, "head", Department), (outsider, "head", Guid.Parse("10000000-0000-0000-0000-000000000002")), (admin, "admin", null) })
            db.Users.Add(new User { Id = id, CognitoSub = id.ToString(), Email = id + "@example.invalid", FullName = "Synthetic", Role = role, DepartmentId = department });
        await db.SaveChangesAsync();
    }
    private async Task<Question> SourceAsync(string status = "Approved", Guid? creator = null)
    {
        var q = new Question
        {
            Id = Guid.NewGuid(),
            SubjectId = Subject,
            TopicId = Topic,
            Grade = 10,
            DepartmentId = Department,
            CreatedBy = creator ?? owner,
            Type = "MultipleChoice",
            Difficulty = "Recognition",
            Stem = "Synthetic question",
            Payload = Payload,
            Status = status,
            ReviewedBy = status == "Approved" ? head : null,
            ReviewedAt = status == "Approved" ? DateTime.UtcNow : null
        };
        await using var db = fixture.CreateContext(); db.Questions.Add(q); await db.SaveChangesAsync(); return q;
    }
    private async Task<ExamDetail> CreateAsync(Guid? creator = null)
    {
        await using var db = fixture.CreateContext(); return await Service(db).CreateAsync(creator ?? owner, new("Synthetic exam", Subject, 10), default);
    }
    private async Task<ExamVersionView> ComposeAsync(Guid id, params ExamItemInput[] items)
    {
        await using var db = fixture.CreateContext(); return await Service(db).UpdateVersionAsync(owner, id, new(45, items), default);
    }
    private async Task<ExamVersionView> ActAsync(Guid actor, Guid id, ExamAction action, string? note = null)
    {
        await using var db = fixture.CreateContext();
        var token = await db.ExamVersions.Where(v => v.Id == id).Select(v => v.SubmittedAt).SingleAsync();
        return await Service(db).TransitionAsync(actor, id, action, token is DateTime at ? new(new DateTimeOffset(at), note) : null, default);
    }

    [PostgresFact]
    // A head may inspect an exam without inheriting access to the author's live private question or its answers.
    public async Task Editable_versions_redact_unreadable_sources_without_hiding_exam_metadata()
    {
        await SeedAsync(); var privateQuestion = await SourceAsync("Draft"); var approved = await SourceAsync();
        var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        foreach (var stage in new[] { "Draft", "Rejected" })
        {
            if (stage == "Rejected")
            {
                await ComposeAsync(id, new ExamItemInput(approved.Id, 1)); await ActAsync(owner, id, ExamAction.Submit);
                await ActAsync(head, id, ExamAction.Reject, "Reason");
                await using var change = fixture.CreateContext();
                await change.Questions.Where(q => q.Id == privateQuestion.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(q => q.Status, "Rejected").SetProperty(q => q.ReviewedBy, head)
                    .SetProperty(q => q.ReviewedAt, DateTime.UtcNow).SetProperty(q => q.ReviewNote, "Private reason"));
            }
            await ComposeAsync(id, new ExamItemInput(privateQuestion.Id, 1), new ExamItemInput(approved.Id, 2));
            await using var read = fixture.CreateContext();
            var hidden = await Service(read).GetVersionAsync(head, id, default);
            Assert.Equal(stage, hidden.Status); Assert.Equal(2, hidden.QuestionCount);
            var item = hidden.Items[0];
            Assert.Null(item.Stem); Assert.Null(item.Payload); Assert.Null(item.Explanation);
            Assert.Null(item.Type); Assert.Null(item.Difficulty); Assert.Null(item.CurrentSourceRevision);
            Assert.Equal("SourceContentUnavailable", Assert.Single(hidden.Warnings).Code);
            Assert.NotNull(hidden.Items[1].Payload); Assert.Null(hidden.ContentHash);
            Assert.NotNull((await Service(read).GetVersionAsync(owner, id, default)).Items[0].Payload);
            Assert.NotNull((await Service(read).GetVersionAsync(admin, id, default)).Items[0].Payload);
            Assert.Equal("NotFound", (await Assert.ThrowsAsync<QuestionException>(() =>
                new QuestionManagement(read, NullLogger<QuestionManagement>.Instance).GetAsync(head, privateQuestion.Id, default))).Code);
        }
    }

    [PostgresFact]
    // Source selection remains scoped to the exam's immutable department after its author transfers to another department.
    public async Task Transferred_author_can_compose_clone_and_submit_their_original_department_exam()
    {
        await SeedAsync(); var q = await SourceAsync(creator: peer); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await ComposeAsync(id, new ExamItemInput(q.Id, 1));
        await using (var db = fixture.CreateContext()) await db.Users.Where(u => u.Id == owner).ExecuteUpdateAsync(s =>
            s.SetProperty(u => u.DepartmentId, Guid.Parse("10000000-0000-0000-0000-000000000002")));
        await ComposeAsync(id, new ExamItemInput(q.Id, 2));
        var submitted = await ActAsync(owner, id, ExamAction.Submit); Assert.Equal("Submitted", submitted.Status);
        Assert.Equal(Department, submitted.DepartmentId); await ActAsync(head, id, ExamAction.Approve);
        await using var clone = fixture.CreateContext();
        var next = await Service(clone).CreateVersionAsync(owner, exam.Id, new(), default);
        Assert.Equal(q.Id, Assert.Single(next.Items).SourceQuestionId); Assert.Empty(next.Warnings);
        Assert.Equal("Submitted", (await ActAsync(owner, next.Id, ExamAction.Submit)).Status);
    }

    [PostgresFact]
    public async Task Invalid_authorization_profiles_challenge_both_reads_and_mutations()
    {
        await SeedAsync(); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await using var corrupt = fixture.CreateContext();
        // Simulate legacy/corrupted profiles only in the disposable test database, where normal CHECKs would prevent them.
        await corrupt.Database.ExecuteSqlRawAsync("ALTER TABLE users DROP CONSTRAINT ck_users_role; ALTER TABLE users DROP CONSTRAINT ck_users_department;");
        foreach (var invalidRole in new[] { true, false })
        {
            await corrupt.Users.Where(u => u.Id == owner).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.Role, invalidRole ? "unknown" : "teacher")
                .SetProperty(u => u.DepartmentId, invalidRole ? Department : (Guid?)null));
            await using var read = fixture.CreateContext();
            Assert.Equal(401, (await Assert.ThrowsAsync<ExamException>(() => Service(read).GetVersionAsync(owner, id, default))).StatusCode);
            Assert.Equal(401, (await Assert.ThrowsAsync<ExamException>(() => ActAsync(owner, id, ExamAction.Submit))).StatusCode);
        }
    }

    [PostgresFact]
    public async Task Editable_responses_hide_old_snapshot_hashes_in_detail_and_summary_queries()
    {
        await SeedAsync(); var q = await SourceAsync(); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await ComposeAsync(id, new ExamItemInput(q.Id, 1)); var frozen = await ActAsync(owner, id, ExamAction.Submit);
        var rejected = await ActAsync(head, id, ExamAction.Reject, "Reason"); Assert.Null(rejected.ContentHash);
        await using var read = fixture.CreateContext();
        Assert.Equal(frozen.ContentHash, (await read.ExamVersions.AsNoTracking().SingleAsync(v => v.Id == id)).ContentHash);
        var detail = await Service(read).GetAsync(owner, exam.Id, default);
        Assert.Null(Assert.Single(detail.Versions).ContentHash); Assert.Equal(1, detail.Versions[0].QuestionCount);
        var page = await Service(read).VersionsAsync(owner, exam.Id, 1, 20, default);
        Assert.Null(Assert.Single(page.Items).ContentHash); Assert.Equal(1, page.Items[0].QuestionCount);
        var archived = await ActAsync(owner, id, ExamAction.Archive);
        Assert.Equal(frozen.ContentHash, archived.ContentHash); Assert.NotNull(archived.Items[0].Payload);
        Assert.Equal(archived.ContentHash, Assert.Single((await Service(read).VersionsAsync(head, exam.Id, 1, 20, default)).Items).ContentHash);
    }

    [PostgresFact]
    public async Task Full_workflow_freezes_snapshots_reopens_and_preserves_publication_history()
    {
        await SeedAsync(); var q = await SourceAsync(); var exam = await CreateAsync(); var id = Assert.Single(exam.Versions).Id;
        Assert.Equal(45, exam.Versions[0].DurationMinutes); Assert.Equal(exam.CreatedAt, exam.UpdatedAt);
        var composed = await ComposeAsync(id, new ExamItemInput(q.Id, 2.5m)); Assert.Equal(2.5m, composed.TotalPoints);
        var first = await ActAsync(owner, id, ExamAction.Submit);
        Assert.Equal("Submitted", first.Status); Assert.NotNull(first.ContentHash); Assert.Null(first.Items[0].CurrentSourceRevision);
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(System.Text.Json.Nodes.JsonNode.Parse(Payload),
            System.Text.Json.Nodes.JsonNode.Parse(first.Items[0].Payload!.Value.GetRawText())));
        var draft = await ActAsync(owner, id, ExamAction.Withdraw); Assert.Null(draft.ContentHash); Assert.Null(draft.SubmittedAt);
        await using (var db = fixture.CreateContext())
            Assert.All(await db.ExamVersionQuestions.Where(i => i.ExamVersionId == id).ToListAsync(), i => Assert.Null(i.PayloadSnapshot));
        var second = await ActAsync(owner, id, ExamAction.Submit); Assert.Equal(first.ContentHash, second.ContentHash); Assert.NotEqual(first.SubmittedAt, second.SubmittedAt);
        await ActAsync(head, id, ExamAction.Reject, "Clarify");
        await ComposeAsync(id, new ExamItemInput(q.Id, 3m));
        var third = await ActAsync(owner, id, ExamAction.Submit); Assert.NotEqual(first.ContentHash, third.ContentHash);
        await ActAsync(head, id, ExamAction.Approve); var published = await ActAsync(admin, id, ExamAction.Publish);
        Assert.Equal(admin, published.PublishedBy!.Id); Assert.NotNull(published.PublishedAt);
        var approved = await ActAsync(admin, id, ExamAction.Unpublish); Assert.Equal(published.ContentHash, approved.ContentHash);
        Assert.Equal(published.PublishedAt, approved.PublishedAt);
        await ActAsync(owner, id, ExamAction.Archive);
        Assert.Equal("InvalidStateTransition", (await Assert.ThrowsAsync<ExamException>(() => ActAsync(admin, id, ExamAction.Publish))).Code);
        await using var verify = fixture.CreateContext();
        Assert.Equal(12, await verify.AuditLogs.CountAsync(a => a.EntityId == id));
    }

    [PostgresFact]
    public async Task Submit_rejects_empty_or_unapproved_sources_without_partial_snapshot_or_audit()
    {
        await SeedAsync(); var q = await SourceAsync("Draft"); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        Assert.Equal("items", (await Assert.ThrowsAsync<ExamException>(() => ActAsync(owner, id, ExamAction.Submit))).Field);
        await ComposeAsync(id, new ExamItemInput(q.Id, 1));
        var error = await Assert.ThrowsAsync<ExamException>(() => ActAsync(owner, id, ExamAction.Submit));
        Assert.Equal("QuestionsNotApproved", error.Code); Assert.Equal(q.Id, Assert.Single(error.InvalidQuestionIds!));
        await using var db = fixture.CreateContext();
        var version = await db.ExamVersions.Include(v => v.Questions).SingleAsync(v => v.Id == id);
        Assert.Equal("Draft", version.Status); Assert.Null(version.ContentHash); Assert.Null(Assert.Single(version.Questions).StemSnapshot);
        Assert.False(await db.AuditLogs.AnyAsync(a => a.EntityId == id && a.Action == "SubmitExamVersion"));
    }

    [PostgresFact]
    public async Task Visibility_separates_published_metadata_from_answers_and_hides_other_versions()
    {
        await SeedAsync(); var q = await SourceAsync(); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await ComposeAsync(id, new ExamItemInput(q.Id, 1));
        await using (var db = fixture.CreateContext())
        {
            Assert.Empty((await Service(db).ListAsync(peer, new(), default)).Items);
            Assert.Equal("NotFound", (await Assert.ThrowsAsync<ExamException>(() => Service(db).GetVersionAsync(peer, id, default))).Code);
        }
        await ActAsync(owner, id, ExamAction.Submit); await ActAsync(head, id, ExamAction.Approve); await ActAsync(admin, id, ExamAction.Publish);
        await using var read = fixture.CreateContext();
        var next = await Service(read).CreateVersionAsync(owner, exam.Id, new(), default);
        Assert.Equal(2, next.VersionNo); Assert.Equal(q.Revision, next.Items[0].SourceRevision);
        var peerExam = await Service(read).GetAsync(peer, exam.Id, default);
        Assert.Single(peerExam.Versions); Assert.Equal(1, peerExam.LatestVersionNo);
        Assert.Single((await Service(read).VersionsAsync(peer, exam.Id, 1, 20, default)).Items);
        Assert.Equal("Forbidden", (await Assert.ThrowsAsync<ExamException>(() => Service(read).GetVersionAsync(peer, id, default))).Code);
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<ExamException>(() => Service(read).GetVersionAsync(peer, next.Id, default))).Code);
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<ExamException>(() => Service(read).GetAsync(outsider, exam.Id, default))).Code);
        Assert.Equal("Published", (await Service(read).GetVersionAsync(head, id, default)).Status);
    }

    [PostgresFact]
    public async Task Review_rechecks_live_actor_self_review_notes_states_and_stale_round_tokens()
    {
        await SeedAsync(); var q = await SourceAsync(); var exam = await CreateAsync(head); var id = exam.Versions[0].Id;
        await using (var db = fixture.CreateContext()) await Service(db).UpdateVersionAsync(head, id, new(45, [new(q.Id, 1)]), default);
        var first = await ActAsync(head, id, ExamAction.Submit);
        Assert.Equal("SelfReview", (await Assert.ThrowsAsync<ExamException>(() => ActAsync(head, id, ExamAction.Approve))).Code);
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<ExamException>(() => ActAsync(outsider, id, ExamAction.Approve))).Code);
        await ActAsync(head, id, ExamAction.Withdraw); await ActAsync(head, id, ExamAction.Submit);
        await using var review = fixture.CreateContext();
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<ExamException>(() => Service(review).TransitionAsync(admin, id,
            ExamAction.Approve, new(new DateTimeOffset(first.SubmittedAt!.Value)), default))).Code);
        Assert.Equal("note", (await Assert.ThrowsAsync<ExamException>(() => ActAsync(admin, id, ExamAction.Reject, " "))).Field);
        var queue = await Service(review).ReviewQueueAsync(admin, new(), default); Assert.Equal(id, Assert.Single(queue.Items).Id);
        Assert.Empty((await Service(review).ReviewQueueAsync(head, new(), default)).Items);
        await review.Users.Where(u => u.Id == admin).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));
        Assert.Equal("Unauthorized", (await Assert.ThrowsAsync<ExamException>(() => ActAsync(admin, id, ExamAction.Approve))).Code);
        Assert.Equal("Submitted", (await review.ExamVersions.AsNoTracking().SingleAsync(v => v.Id == id)).Status);
    }

    [PostgresFact]
    public async Task Clone_omits_archived_sources_and_preserves_frozen_originals()
    {
        await SeedAsync(); var q = await SourceAsync(); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await ComposeAsync(id, new ExamItemInput(q.Id, 2)); await ActAsync(owner, id, ExamAction.Submit); var approved = await ActAsync(head, id, ExamAction.Approve);
        await using (var db = fixture.CreateContext())
            await new QuestionManagement(db, NullLogger<QuestionManagement>.Instance).TransitionAsync(owner, q.Id, QuestionAction.Archive, null, default);
        await using var clone = fixture.CreateContext();
        var next = await Service(clone).CreateVersionAsync(owner, exam.Id, new(id), default);
        Assert.Empty(next.Items); Assert.Equal(q.Id, Assert.Single(next.Warnings).SourceQuestionId);
        Assert.Equal(approved.ContentHash, (await Service(clone).GetVersionAsync(owner, id, default)).ContentHash);
        Assert.Equal("Synthetic question", (await Service(clone).GetVersionAsync(owner, id, default)).Items[0].Stem);
        Assert.Equal("Conflict", (await Assert.ThrowsAsync<ExamException>(() => Service(clone).CreateVersionAsync(owner, exam.Id, new(), default))).Code);
    }

    [PostgresFact]
    public async Task Concurrent_publication_and_version_creation_have_one_winner_each()
    {
        await SeedAsync(); var q = await SourceAsync(); var exam = await CreateAsync(); var one = exam.Versions[0].Id;
        await ComposeAsync(one, new ExamItemInput(q.Id, 1)); await ActAsync(owner, one, ExamAction.Submit); await ActAsync(head, one, ExamAction.Approve);
        async Task<ExamVersionView?> Clone()
        {
            await using var db = fixture.CreateContext();
            try { return await Service(db).CreateVersionAsync(owner, exam.Id, new(), default); }
            catch (ExamException e) { Assert.Equal(409, e.StatusCode); return null; }
        }
        var copies = await Task.WhenAll(Clone(), Clone()); var two = Assert.Single(copies, c => c != null)!.Id;
        await ActAsync(owner, two, ExamAction.Submit); await ActAsync(head, two, ExamAction.Approve);
        async Task<bool> Publish(Guid id)
        {
            try { await ActAsync(admin, id, ExamAction.Publish); return true; }
            catch (ExamException e) { Assert.Equal("PublishedVersionExists", e.Code); return false; }
        }
        Assert.Single(await Task.WhenAll(Publish(one), Publish(two)), x => x);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.ExamVersions.CountAsync(v => v.ExamId == exam.Id && v.Status == "Published"));
        Assert.Equal(1, await verify.AuditLogs.CountAsync(a => a.Action == "PublishExamVersion"));
    }

    [PostgresFact]
    public async Task Rejected_archival_preserves_existing_data_without_capturing_private_sources()
    {
        await SeedAsync(); var q = await SourceAsync(); var ownDraft = await SourceAsync("Draft"); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await ComposeAsync(id, new ExamItemInput(q.Id, 1)); await ActAsync(owner, id, ExamAction.Submit); await ActAsync(head, id, ExamAction.Reject, "Reason");
        await ComposeAsync(id, new ExamItemInput(ownDraft.Id, 3)); var archived = await ActAsync(owner, id, ExamAction.Archive);
        Assert.Equal("Archived", archived.Status); Assert.Null(archived.Items[0].Stem); Assert.Null(archived.ContentHash);
        Assert.Null(archived.Items[0].Payload); Assert.Equal("SnapshotUnavailable", Assert.Single(archived.Warnings).Code);
        await using (var db = fixture.CreateContext())
        {
            Assert.Null((await db.ExamVersionQuestions.SingleAsync(i => i.ExamVersionId == id)).PayloadSnapshot);
            Assert.Null((await Service(db).GetVersionAsync(head, id, default)).Items[0].Payload);
        }
        Assert.Equal("InvalidStateTransition", (await Assert.ThrowsAsync<ExamException>(() => ComposeAsync(id, new ExamItemInput(q.Id, 1)))).Code);
    }

    [PostgresFact]
    // Wait for an actual blocking dependency before committing a source change; the writer must re-read after its share lock.
    public async Task Composition_rechecks_source_after_waiting_for_a_concurrent_archive()
    {
        await SeedAsync(); var q = await SourceAsync(); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await using var holder = new NpgsqlConnection(fixture.ConnectionString); await holder.OpenAsync();
        await using var transaction = await holder.BeginTransactionAsync();
        await using (var change = new NpgsqlCommand("UPDATE questions SET status = 'Archived' WHERE id = @id", holder, transaction))
        { change.Parameters.AddWithValue("id", q.Id); await change.ExecuteNonQueryAsync(); }
        await using var writer = fixture.CreateContext();
        var task = Service(writer).UpdateVersionAsync(owner, id, new(45, [new(q.Id, 1)]), default);
        await using var observer = new NpgsqlConnection(fixture.ConnectionString); await observer.OpenAsync();
        var waiting = false; var deadline = DateTime.UtcNow.AddSeconds(1.5);
        while (DateTime.UtcNow < deadline)
        {
            await using var probe = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE @holder = ANY(pg_blocking_pids(pid)))", observer);
            probe.Parameters.AddWithValue("holder", holder.ProcessID);
            if ((bool)(await probe.ExecuteScalarAsync())!) { waiting = true; break; }
            await Task.Delay(10);
        }
        Assert.True(waiting, "The composition writer must actually wait for its source lock.");
        await transaction.CommitAsync();
        Assert.Equal("items", (await Assert.ThrowsAsync<ExamException>(() => task)).Field);
        Assert.False(await observerState());
        async Task<bool> observerState()
        {
            await using var verify = fixture.CreateContext(); return await verify.ExamVersionQuestions.AnyAsync(i => i.ExamVersionId == id);
        }
    }

    [PostgresFact]
    public async Task Invisible_version_requests_do_not_wait_for_a_private_row_lock()
    {
        await SeedAsync(); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await using var holder = fixture.CreateContext(); await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM exam_versions WHERE id = {id} FOR UPDATE");
        await using var reader = fixture.CreateContext(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        var error = await Assert.ThrowsAsync<ExamException>(() => Service(reader).TransitionAsync(peer, id, ExamAction.Withdraw, null, timeout.Token));
        Assert.Equal(404, error.StatusCode);
    }

    [PostgresFact]
    public async Task A6_source_guards_and_A7_snapshot_reads_remain_consistent_across_approval()
    {
        await SeedAsync(); var q = await SourceAsync(creator: peer); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await ComposeAsync(id, new ExamItemInput(q.Id, 1));
        var input = new QuestionInput(Subject, Topic, 10, "MultipleChoice", "Recognition", "Changed source", JsonSerializer.Deserialize<JsonElement>(Payload));
        foreach (var submitted in new[] { false, true })
        {
            if (submitted) await ActAsync(owner, id, ExamAction.Submit);
            await using var db = fixture.CreateContext();
            var error = await Assert.ThrowsAsync<QuestionException>(() => new QuestionManagement(db, NullLogger<QuestionManagement>.Instance).UpdateAsync(peer, q.Id, input, default));
            Assert.Equal("QuestionReferenced", error.Code); Assert.Equal(id, Assert.Single(error.References!.Items).VersionId);
        }
        var frozen = await ActAsync(head, id, ExamAction.Approve);
        await using (var db = fixture.CreateContext())
            await new QuestionManagement(db, NullLogger<QuestionManagement>.Instance).UpdateAsync(peer, q.Id, input, default);
        await using var verify = fixture.CreateContext();
        var result = await Service(verify).GetVersionAsync(owner, id, default);
        Assert.Equal(frozen.ContentHash, result.ContentHash); Assert.Equal("Synthetic question", result.Items[0].Stem);
        Assert.Equal(1, result.Items[0].SourceRevision); Assert.Null(result.Items[0].CurrentSourceRevision);
        Assert.Equal(2, (await verify.Questions.AsNoTracking().SingleAsync(x => x.Id == q.Id)).Revision);
    }

    [PostgresFact]
    public async Task Audit_failure_rolls_back_snapshot_and_submission()
    {
        await SeedAsync(); var q = await SourceAsync(); var exam = await CreateAsync(); var id = exam.Versions[0].Id;
        await ComposeAsync(id, new ExamItemInput(q.Id, 1));
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_exam_audit() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN
            IF NEW.action = 'SubmitExamVersion' THEN RAISE EXCEPTION 'Synthetic audit failure' USING ERRCODE = '23514'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER test_exam_audit BEFORE INSERT ON audit_logs FOR EACH ROW EXECUTE FUNCTION fail_exam_audit();
            """);
        await Assert.ThrowsAsync<ExamException>(() => ActAsync(owner, id, ExamAction.Submit));
        var version = await db.ExamVersions.AsNoTracking().Include(v => v.Questions).SingleAsync(v => v.Id == id);
        Assert.Equal("Draft", version.Status); Assert.Null(version.ContentHash); Assert.Null(Assert.Single(version.Questions).PayloadSnapshot);
        Assert.False(await db.AuditLogs.AnyAsync(a => a.EntityId == id && a.Action == "SubmitExamVersion"));
    }
}
