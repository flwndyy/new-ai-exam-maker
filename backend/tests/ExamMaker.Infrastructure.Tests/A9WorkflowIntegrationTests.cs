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

    // Use a fresh DbContext per operation to mirror HTTP request-scoped tracking and ExecuteUpdateAsync behavior.
    private async Task<T> RunQuestionAsync<T>(Func<QuestionManagement, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new QuestionManagement(db, NullLogger<QuestionManagement>.Instance));
    }

    private async Task<T> RunExamAsync<T>(Func<ExamManagement, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new ExamManagement(db, NullLogger<ExamManagement>.Instance));
    }

    /// <summary>Exercise the full Question state machine (Draft -> Submitted -> Rejected -> Approved -> Archived) and permission denials against PostgreSQL.</summary>
    [PostgresFact]
    public async Task Question_full_lifecycle_transitions_and_permission_denials_persist_on_postgres()
    {
        await SeedUsersAsync();

        // Admin cannot create content.
        var adminCreate = await Assert.ThrowsAsync<QuestionException>(() => RunQuestionAsync(q => q.CreateAsync(
            admin, new(SubjectA, TopicA, 10, "MultipleChoice", "Recognition", "Blocked", ValidPayload), CancellationToken.None)));
        Assert.Equal(403, adminCreate.StatusCode);
        Assert.Equal("Forbidden", adminCreate.Code);

        // Create Draft.
        var created = await RunQuestionAsync(q => q.CreateAsync(
            teacherA, new(SubjectA, TopicA, 10, "MultipleChoice", "Recognition", "Initial stem", ValidPayload), CancellationToken.None));
        Assert.Equal("Draft", created.Status);

        // Invalid transitions from Draft: use an actor who can see the Draft (Admin for Approve, owner for Withdraw/Archive)
        // so the state machine returns 409 InvalidStateTransition rather than 404 NotFound.
        var draftApprove = await Assert.ThrowsAsync<QuestionException>(() => RunQuestionAsync(q =>
            q.TransitionAsync(admin, created.Id, QuestionAction.Approve, null, CancellationToken.None, DateTimeOffset.UtcNow)));
        Assert.Equal(409, draftApprove.StatusCode);
        Assert.Equal("InvalidStateTransition", draftApprove.Code);

        var draftWithdraw = await Assert.ThrowsAsync<QuestionException>(() => RunQuestionAsync(q =>
            q.TransitionAsync(teacherA, created.Id, QuestionAction.Withdraw, null, CancellationToken.None)));
        Assert.Equal(409, draftWithdraw.StatusCode);
        Assert.Equal("InvalidStateTransition", draftWithdraw.Code);

        var draftArchive = await Assert.ThrowsAsync<QuestionException>(() => RunQuestionAsync(q =>
            q.TransitionAsync(teacherA, created.Id, QuestionAction.Archive, null, CancellationToken.None)));
        Assert.Equal(409, draftArchive.StatusCode);
        Assert.Equal("InvalidStateTransition", draftArchive.Code);

        // Submit -> Withdraw -> Submit.
        var submitted = await RunQuestionAsync(q => q.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None));
        Assert.Equal("Submitted", submitted.Status);
        var withdrawn = await RunQuestionAsync(q => q.TransitionAsync(teacherA, created.Id, QuestionAction.Withdraw, null, CancellationToken.None));
        Assert.Equal("Draft", withdrawn.Status);
        submitted = await RunQuestionAsync(q => q.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None));
        Assert.Equal("Submitted", submitted.Status);

        // Peer teacher and out-of-department head cannot see private Submitted questions (404 NotFound hides resource existence).
        var peerApprove = await Assert.ThrowsAsync<QuestionException>(() => RunQuestionAsync(q =>
            q.TransitionAsync(peerA, created.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value))));
        Assert.Equal(404, peerApprove.StatusCode);
        Assert.Equal("NotFound", peerApprove.Code);

        var otherHeadApprove = await Assert.ThrowsAsync<QuestionException>(() => RunQuestionAsync(q =>
            q.TransitionAsync(headB, created.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value))));
        Assert.Equal(404, otherHeadApprove.StatusCode);
        Assert.Equal("NotFound", otherHeadApprove.Code);

        // Reject by Head A (requires note) -> Resubmit -> Approve by Head A -> Archive by Head A.
        var rejected = await RunQuestionAsync(q => q.TransitionAsync(headA, created.Id, QuestionAction.Reject, "Need clearer stem", CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value)));
        Assert.Equal("Rejected", rejected.Status);

        submitted = await RunQuestionAsync(q => q.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None));
        var approved = await RunQuestionAsync(q => q.TransitionAsync(headA, created.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value)));
        Assert.Equal("Approved", approved.Status);

        // Editing Approved content reopens to Draft and increments Revision; resubmit and approve before archiving.
        var reopened = await RunQuestionAsync(q => q.UpdateAsync(
            teacherA, created.Id, new(SubjectA, TopicA, 10, "MultipleChoice", "Recognition", "Updated stem", ValidPayload), CancellationToken.None));
        Assert.Equal("Draft", reopened.Status);
        Assert.Equal(2, reopened.Revision);

        submitted = await RunQuestionAsync(q => q.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None));
        approved = await RunQuestionAsync(q => q.TransitionAsync(headA, created.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(submitted.SubmittedAt!.Value)));
        var archived = await RunQuestionAsync(q => q.TransitionAsync(headA, created.Id, QuestionAction.Archive, null, CancellationToken.None));
        Assert.Equal("Archived", archived.Status);

        // Terminal Archived rejects further transitions.
        var afterArchive = await Assert.ThrowsAsync<QuestionException>(() => RunQuestionAsync(q =>
            q.TransitionAsync(teacherA, created.Id, QuestionAction.Submit, null, CancellationToken.None)));
        Assert.Equal(409, afterArchive.StatusCode);
        Assert.Equal("InvalidStateTransition", afterArchive.Code);

        // Creation, content edits, and every workflow transition are recorded in exact chronological order.
        await using var verifyDb = fixture.CreateContext();
        var auditActions = await verifyDb.AuditLogs
            .Where(x => x.EntityType == "Question" && x.EntityId == created.Id)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => x.Action)
            .ToListAsync();
        Assert.Equal(
            [
                "CreateQuestion",
                "SubmitQuestion",
                "WithdrawQuestion",
                "SubmitQuestion",
                "RejectQuestion",
                "SubmitQuestion",
                "ApproveQuestion",
                "UpdateQuestion",
                "SubmitQuestion",
                "ApproveQuestion",
                "ArchiveQuestion"
            ],
            auditActions);
    }

    /// <summary>Exercise the full ExamVersion state machine (Draft -> Submitted -> Rejected -> Approved -> Published -> Approved -> Archived) against PostgreSQL.</summary>
    [PostgresFact]
    public async Task ExamVersion_full_lifecycle_transitions_and_permission_denials_persist_on_postgres()
    {
        await SeedUsersAsync();

        // Prepare one Approved source question in DeptA.
        var q = await RunQuestionAsync(svc => svc.CreateAsync(
            teacherA, new(SubjectA, TopicA, 10, "MultipleChoice", "Recognition", "Exam source", ValidPayload), CancellationToken.None));
        var qSub = await RunQuestionAsync(svc => svc.TransitionAsync(teacherA, q.Id, QuestionAction.Submit, null, CancellationToken.None));
        await RunQuestionAsync(svc => svc.TransitionAsync(headA, q.Id, QuestionAction.Approve, null, CancellationToken.None, new DateTimeOffset(qSub.SubmittedAt!.Value)));

        // Create Exam + Draft v1 and compose.
        var examDetail = await RunExamAsync(e => e.CreateAsync(teacherA, new("Midterm A9", SubjectA, 10), CancellationToken.None));
        var v1Id = examDetail.Versions[0].Id;
        await RunExamAsync(e => e.UpdateVersionAsync(teacherA, v1Id, new(45, [new(q.Id, 1.0m)]), CancellationToken.None));

        // Submit -> Withdraw -> Submit -> Reject -> Submit -> Approve (Admin).
        var v1 = await RunExamAsync(e => e.TransitionAsync(teacherA, v1Id, ExamAction.Submit, null, CancellationToken.None));
        Assert.Equal("Submitted", v1.Status);

        // Author cannot self-review, and out-of-department head cannot access private Submitted versions (404 NotFound).
        var authorSelfReview = await Assert.ThrowsAsync<ExamException>(() => RunExamAsync(e =>
            e.TransitionAsync(teacherA, v1Id, ExamAction.Approve, new(new DateTimeOffset(v1.SubmittedAt!.Value), null), CancellationToken.None)));
        Assert.Equal(403, authorSelfReview.StatusCode);
        Assert.Equal("Forbidden", authorSelfReview.Code);

        var otherHeadReview = await Assert.ThrowsAsync<ExamException>(() => RunExamAsync(e =>
            e.TransitionAsync(headB, v1Id, ExamAction.Approve, new(new DateTimeOffset(v1.SubmittedAt!.Value), null), CancellationToken.None)));
        Assert.Equal(404, otherHeadReview.StatusCode);
        Assert.Equal("NotFound", otherHeadReview.Code);

        // Head author also receives 403 SelfReview when attempting to approve their own exam version.
        var headExam = await RunExamAsync(e => e.CreateAsync(headA, new("Head Midterm", SubjectA, 10), CancellationToken.None));
        var headV1Id = headExam.Versions[0].Id;
        await RunExamAsync(e => e.UpdateVersionAsync(headA, headV1Id, new(45, [new(q.Id, 1.0m)]), CancellationToken.None));
        var headV1 = await RunExamAsync(e => e.TransitionAsync(headA, headV1Id, ExamAction.Submit, null, CancellationToken.None));
        var headSelfReview = await Assert.ThrowsAsync<ExamException>(() => RunExamAsync(e =>
            e.TransitionAsync(headA, headV1Id, ExamAction.Approve, new(new DateTimeOffset(headV1.SubmittedAt!.Value), null), CancellationToken.None)));
        Assert.Equal(403, headSelfReview.StatusCode);
        Assert.Equal("SelfReview", headSelfReview.Code);

        v1 = await RunExamAsync(e => e.TransitionAsync(teacherA, v1Id, ExamAction.Withdraw, null, CancellationToken.None));
        Assert.Equal("Draft", v1.Status);

        v1 = await RunExamAsync(e => e.TransitionAsync(teacherA, v1Id, ExamAction.Submit, null, CancellationToken.None));
        v1 = await RunExamAsync(e => e.TransitionAsync(headA, v1Id, ExamAction.Reject, new(new DateTimeOffset(v1.SubmittedAt!.Value), "Fix rubric"), CancellationToken.None));
        Assert.Equal("Rejected", v1.Status);

        v1 = await RunExamAsync(e => e.TransitionAsync(teacherA, v1Id, ExamAction.Submit, null, CancellationToken.None));
        v1 = await RunExamAsync(e => e.TransitionAsync(admin, v1Id, ExamAction.Approve, new(new DateTimeOffset(v1.SubmittedAt!.Value), null), CancellationToken.None));
        Assert.Equal("Approved", v1.Status);

        // Only Admin may Publish/Unpublish; Teacher and Head receive 403 Forbidden.
        var teacherPublish = await Assert.ThrowsAsync<ExamException>(() => RunExamAsync(e =>
            e.TransitionAsync(teacherA, v1Id, ExamAction.Publish, null, CancellationToken.None)));
        Assert.Equal(403, teacherPublish.StatusCode);
        Assert.Equal("Forbidden", teacherPublish.Code);

        var headPublish = await Assert.ThrowsAsync<ExamException>(() => RunExamAsync(e =>
            e.TransitionAsync(headA, v1Id, ExamAction.Publish, null, CancellationToken.None)));
        Assert.Equal(403, headPublish.StatusCode);
        Assert.Equal("Forbidden", headPublish.Code);

        v1 = await RunExamAsync(e => e.TransitionAsync(admin, v1Id, ExamAction.Publish, null, CancellationToken.None));
        Assert.Equal("Published", v1.Status);

        var teacherUnpublish = await Assert.ThrowsAsync<ExamException>(() => RunExamAsync(e =>
            e.TransitionAsync(teacherA, v1Id, ExamAction.Unpublish, null, CancellationToken.None)));
        Assert.Equal(403, teacherUnpublish.StatusCode);
        Assert.Equal("Forbidden", teacherUnpublish.Code);

        var headUnpublish = await Assert.ThrowsAsync<ExamException>(() => RunExamAsync(e =>
            e.TransitionAsync(headA, v1Id, ExamAction.Unpublish, null, CancellationToken.None)));
        Assert.Equal(403, headUnpublish.StatusCode);
        Assert.Equal("Forbidden", headUnpublish.Code);

        // Unpublish returns to Approved (there is no Unpublished state), then Archive.
        v1 = await RunExamAsync(e => e.TransitionAsync(admin, v1Id, ExamAction.Unpublish, null, CancellationToken.None));
        Assert.Equal("Approved", v1.Status);

        v1 = await RunExamAsync(e => e.TransitionAsync(headA, v1Id, ExamAction.Archive, null, CancellationToken.None));
        Assert.Equal("Archived", v1.Status);

        // Terminal Archived rejects transitions.
        var afterArchive = await Assert.ThrowsAsync<ExamException>(() => RunExamAsync(e =>
            e.TransitionAsync(admin, v1Id, ExamAction.Publish, null, CancellationToken.None)));
        Assert.Equal(409, afterArchive.StatusCode);
        Assert.Equal("InvalidStateTransition", afterArchive.Code);

        // Verify the complete ExamVersion audit trail in chronological order.
        await using var verifyDb = fixture.CreateContext();
        var versionAuditActions = await verifyDb.AuditLogs
            .Where(x => x.EntityType == "ExamVersion" && x.EntityId == v1Id)
            .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id)
            .Select(x => x.Action)
            .ToListAsync();
        Assert.Equal(
            [
                "CreateExamVersion",
                "UpdateExamVersion",
                "SubmitExamVersion",
                "WithdrawExamVersion",
                "SubmitExamVersion",
                "RejectExamVersion",
                "SubmitExamVersion",
                "ApproveExamVersion",
                "PublishExamVersion",
                "UnpublishExamVersion",
                "ArchiveExamVersion"
            ],
            versionAuditActions);
    }
}
