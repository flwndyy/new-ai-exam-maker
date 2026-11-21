using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Exams;
using ExamMaker.Application.Questions;
using ExamMaker.Domain.Users;
using ExamMaker.Infrastructure.Exams;
using ExamMaker.Infrastructure.Persistence;
using ExamMaker.Infrastructure.Questions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExamMaker.Infrastructure.Tests;

public sealed class A9WorkflowIntegrationTests : IAsyncLifetime
{
    private readonly PostgresFixture fixture = new();
    private static readonly Guid DeptA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid DeptB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid SubjectA = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid TopicA = Guid.Parse("30000000-0000-0000-0000-000000000001");
    private static readonly JsonElement ValidPayload = JsonDocument.Parse(
        """{"options":[{"label":"A","text":"One"},{"label":"B","text":"Two"},{"label":"C","text":"Three"},{"label":"D","text":"Four"}],"correctLabel":"A"}""").RootElement;

    private readonly Guid teacherA = Guid.NewGuid();
    private readonly Guid peerA = Guid.NewGuid();
    private readonly Guid headA = Guid.NewGuid();
    private readonly Guid headB = Guid.NewGuid();
    private readonly Guid admin = Guid.NewGuid();

    public Task InitializeAsync() => fixture.InitializeAsync();
    public Task DisposeAsync() => fixture.DisposeAsync();

    private async Task SeedUsersAsync()
    {
        await using var db = fixture.CreateContext();
        db.Users.AddRange(
            new User { Id = teacherA, CognitoSub = teacherA.ToString(), Email = "ta@example.invalid", FullName = "Teacher A", Role = Roles.Teacher, DepartmentId = DeptA },
            new User { Id = peerA, CognitoSub = peerA.ToString(), Email = "pa@example.invalid", FullName = "Peer A", Role = Roles.Teacher, DepartmentId = DeptA },
            new User { Id = headA, CognitoSub = headA.ToString(), Email = "ha@example.invalid", FullName = "Head A", Role = Roles.Head, DepartmentId = DeptA },
            new User { Id = headB, CognitoSub = headB.ToString(), Email = "hb@example.invalid", FullName = "Head B", Role = Roles.Head, DepartmentId = DeptB },
            new User { Id = admin, CognitoSub = admin.ToString(), Email = "ad@example.invalid", FullName = "Admin", Role = Roles.Admin, DepartmentId = null });
        await db.SaveChangesAsync();
    }

    private static QuestionManagement Questions(ExamMakerDbContext db) => new(db, NullLogger<QuestionManagement>.Instance);
    private static ExamManagement Exams(ExamMakerDbContext db) => new(db, NullLogger<ExamManagement>.Instance);

    [PostgresFact]
    /// <summary>Exercise the full Question state machine (Draft -> Submitted -> Rejected -> Approved -> Archived) and permission denials against PostgreSQL.</summary>
    public async Task Question_full_lifecycle_transitions_and_permission_denials_persist_on_postgres()
    {
        await SeedUsersAsync();
        await using var db = fixture.CreateContext();
        var questions = Questions(db);

        // Admin cannot create content.
        var adminCreate = await Assert.ThrowsAsync<QuestionException>(() => questions.CreateAsync(
            admin, new(SubjectA, TopicA, 10, "MultipleChoice", "Recognition", "Blocked", ValidPayload), CancellationToken.None));
        Assert.Equal(403, adminCreate.StatusCode);

        // Create Draft.
        var created = await questions.CreateAsync(
            teacherA, new(SubjectA, TopicA, 10, "MultipleChoice", "Recognition", "Initial stem", ValidPayload), CancellationToken.None);
        Assert.Equal("Draft", created.Status);

        // Invalid transition from Draft: Approve/Withdraw/Archive must fail.
        await Assert.ThrowsAsync<QuestionException>(() => questions.TransitionAsync(headA, created.Id, QuestionAction.Approve, null, CancellationToken.None));
        await Assert.ThrowsAsync<QuestionException>(() => questions.TransitionAsync(teacherA, created.Id, QuestionAction.Withdraw, null, CancellationToken.None));
        await Assert.ThrowsAsync<QuestionException>(() => questions.TransitionAsync(teacherA, created.Id, QuestionAction.Archive, null, CancellationToken.None));

        // Submit -> Withdraw -> Submit.
        var submitted = await questions.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None);
        Assert.Equal("Submitted", submitted.Status);
        var withdrawn = await questions.TransitionAsync(teacherA, created.Id, QuestionAction.Withdraw, null, CancellationToken.None);
        Assert.Equal("Draft", withdrawn.Status);
        submitted = await questions.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None);
        Assert.Equal("Submitted", submitted.Status);

        // Peer teacher and out-of-department head cannot approve.
        var peerApprove = await Assert.ThrowsAsync<QuestionException>(() =>
            questions.TransitionAsync(peerA, created.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value)));
        Assert.Equal(403, peerApprove.StatusCode);
        var otherHeadApprove = await Assert.ThrowsAsync<QuestionException>(() =>
            questions.TransitionAsync(headB, created.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value)));
        Assert.Equal(403, otherHeadApprove.StatusCode);

        // Reject by Head A (requires note) -> Resubmit -> Approve by Head A -> Archive by Head A.
        var rejected = await questions.TransitionAsync(headA, created.Id, QuestionAction.Reject, "Need clearer stem", CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value));
        Assert.Equal("Rejected", rejected.Status);

        submitted = await questions.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None);
        var approved = await questions.TransitionAsync(headA, created.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value));
        Assert.Equal("Approved", approved.Status);

        // Editing Approved content reopens to Draft and increments Revision; resubmit and approve before archiving.
        var reopened = await questions.UpdateAsync(
            teacherA, created.Id, new(SubjectA, TopicA, 10, "MultipleChoice", "Recognition", "Updated stem", ValidPayload), CancellationToken.None);
        Assert.Equal("Draft", reopened.Status);
        Assert.Equal(2, reopened.Revision);

        submitted = await questions.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None);
        approved = await questions.TransitionAsync(headA, created.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value));
        var archived = await questions.TransitionAsync(headA, created.Id, QuestionAction.Archive, null, CancellationToken.None);
        Assert.Equal("Archived", archived.Status);

        // Terminal Archived rejects further transitions.
        var afterArchive = await Assert.ThrowsAsync<QuestionException>(() =>
            questions.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None));
        Assert.Equal(409, afterArchive.StatusCode);

        // Every successful transition left an audit trail.
        var auditCount = await db.AuditLogs.CountAsync(x => x.EntityType == "Question" && x.EntityId == created.Id);
        Assert.Equal(11, auditCount);
    }

    [PostgresFact]
    /// <summary>Exercise the full ExamVersion state machine (Draft -> Submitted -> Rejected -> Approved -> Published -> Approved -> Archived) against PostgreSQL.</summary>
    public async Task ExamVersion_full_lifecycle_transitions_and_permission_denials_persist_on_postgres()
    {
        await SeedUsersAsync();
        await using var db = fixture.CreateContext();
        var questions = Questions(db);
        var exams = Exams(db);

        // Prepare one Approved source question in DeptA.
        var q = await questions.CreateAsync(
            teacherA, new(SubjectA, TopicA, 10, "MultipleChoice", "Recognition", "Exam source", ValidPayload), CancellationToken.None);
        var qSub = await questions.TransitionAsync(teacherA, q.Id, QuestionAction.Submit, null, CancellationToken.None);
        await questions.TransitionAsync(headA, q.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(qSub.SubmittedAt!.Value));

        // Create Exam + Draft v1 and compose.
        var examDetail = await exams.CreateAsync(teacherA, new("Midterm A9", SubjectA, 10), CancellationToken.None);
        var v1Id = examDetail.Versions[0].Id;
        await exams.UpdateVersionAsync(teacherA, v1Id, new(45, [new(q.Id, 1.0m)]), CancellationToken.None);

        // Submit -> Withdraw -> Submit -> Reject -> Submit -> Approve (Admin).
        var v1 = await exams.TransitionAsync(teacherA, v1Id, ExamAction.Submit, null, CancellationToken.None);
        Assert.Equal("Submitted", v1.Status);

        v1 = await exams.TransitionAsync(teacherA, v1Id, ExamAction.Withdraw, null, CancellationToken.None);
        Assert.Equal("Draft", v1.Status);

        v1 = await exams.TransitionAsync(teacherA, v1Id, ExamAction.Submit, null, CancellationToken.None);
        v1 = await exams.TransitionAsync(headA, v1Id, ExamAction.Reject, new(new DateTimeOffset(v1.SubmittedAt!.Value), "Fix rubric"), CancellationToken.None);
        Assert.Equal("Rejected", v1.Status);

        v1 = await exams.TransitionAsync(teacherA, v1Id, ExamAction.Submit, null, CancellationToken.None);
        v1 = await exams.TransitionAsync(admin, v1Id, ExamAction.Approve, new(new DateTimeOffset(v1.SubmittedAt!.Value), null), CancellationToken.None);
        Assert.Equal("Approved", v1.Status);

        // Only Admin may Publish/Unpublish; Teacher and Head receive 403.
        var headPublish = await Assert.ThrowsAsync<ExamException>(() =>
            exams.TransitionAsync(headA, v1Id, ExamAction.Publish, null, CancellationToken.None));
        Assert.Equal(403, headPublish.StatusCode);

        v1 = await exams.TransitionAsync(admin, v1Id, ExamAction.Publish, null, CancellationToken.None);
        Assert.Equal("Published", v1.Status);

        // Unpublish returns to Approved (there is no Unpublished state), then Archive.
        v1 = await exams.TransitionAsync(admin, v1Id, ExamAction.Unpublish, null, CancellationToken.None);
        Assert.Equal("Approved", v1.Status);

        v1 = await exams.TransitionAsync(headA, v1Id, ExamAction.Archive, null, CancellationToken.None);
        Assert.Equal("Archived", v1.Status);

        // Terminal Archived rejects transitions.
        var afterArchive = await Assert.ThrowsAsync<ExamException>(() =>
            exams.TransitionAsync(admin, v1Id, ExamAction.Publish, null, CancellationToken.None));
        Assert.Equal(409, afterArchive.StatusCode);
    }
}
