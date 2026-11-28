using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Exams;
using ExamMaker.Application.Questions;
using ExamMaker.Domain.Exams;
using ExamMaker.Domain.Questions;

namespace ExamMaker.Api.Tests;

public sealed class A9PermissionMatrixTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();
    private static readonly Guid DeptA = Guid.NewGuid();
    private static readonly Guid DeptB = Guid.NewGuid();
    private static readonly Guid SubjectId = Guid.NewGuid();
    private static readonly Guid TopicId = Guid.NewGuid();
    private static readonly JsonElement ValidPayload = JsonDocument.Parse(
        """{"options":[{"label":"A","text":"One"},{"label":"B","text":"Two"},{"label":"C","text":"Three"},{"label":"D","text":"Four"}],"correctLabel":"A"}""").RootElement;

    public enum ActorKind
    {
        TeacherOwner,
        InactiveTeacherOwner,
        TeacherPeerSameDept,
        TeacherOtherDept,
        HeadOwner,
        InactiveHeadOwner,
        HeadPeerSameDept,
        InactiveHeadPeerSameDept,
        HeadOtherDept,
        Admin,
        InactiveAdmin
    }

    private static AuthenticatedUser Actor(ActorKind kind) => kind switch
    {
        ActorKind.TeacherOwner => new(OwnerId, "sub-1", "t1@example.invalid", "Teacher Owner", Roles.Teacher, DeptA, null, true),
        ActorKind.InactiveTeacherOwner => new(OwnerId, "sub-1b", "t1b@example.invalid", "Inactive Teacher Owner", Roles.Teacher, DeptA, null, false),
        ActorKind.TeacherPeerSameDept => new(Guid.NewGuid(), "sub-2", "t2@example.invalid", "Teacher Peer", Roles.Teacher, DeptA, null, true),
        ActorKind.TeacherOtherDept => new(Guid.NewGuid(), "sub-3", "t3@example.invalid", "Teacher Other", Roles.Teacher, DeptB, null, true),
        ActorKind.HeadOwner => new(OwnerId, "sub-4", "h1@example.invalid", "Head Owner", Roles.Head, DeptA, null, true),
        ActorKind.InactiveHeadOwner => new(OwnerId, "sub-4b", "h1b@example.invalid", "Inactive Head Owner", Roles.Head, DeptA, null, false),
        ActorKind.HeadPeerSameDept => new(Guid.NewGuid(), "sub-5", "h2@example.invalid", "Head Peer", Roles.Head, DeptA, null, true),
        ActorKind.InactiveHeadPeerSameDept => new(Guid.NewGuid(), "sub-5b", "h2b@example.invalid", "Inactive Head Peer", Roles.Head, DeptA, null, false),
        ActorKind.HeadOtherDept => new(Guid.NewGuid(), "sub-6", "h3@example.invalid", "Head Other", Roles.Head, DeptB, null, true),
        ActorKind.Admin => new(Guid.NewGuid(), "sub-7", "a1@example.invalid", "Admin", Roles.Admin, null, null, true),
        ActorKind.InactiveAdmin => new(Guid.NewGuid(), "sub-8", "a2@example.invalid", "Inactive", Roles.Admin, null, null, false),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static Question NewQuestion(string status = "Draft") => new()
    {
        Id = Guid.NewGuid(),
        SubjectId = SubjectId,
        TopicId = TopicId,
        Grade = 10,
        DepartmentId = DeptA,
        CreatedBy = OwnerId,
        Type = "MultipleChoice",
        Difficulty = "Recognition",
        Stem = "Original stem",
        Payload = ValidPayload.GetRawText(),
        Status = status,
        Source = "Manual",
        UpdatedAt = DateTime.UnixEpoch
    };

    private static Exam NewExam() => new()
    {
        Id = Guid.NewGuid(),
        Title = "Synthetic exam",
        SubjectId = SubjectId,
        Grade = 10,
        DepartmentId = DeptA,
        CreatedBy = OwnerId
    };

    public static IEnumerable<object[]> QuestionVisibilityCases()
    {
        foreach (var kind in Enum.GetValues<ActorKind>())
            foreach (var status in QuestionRules.States)
            {
                var expected = kind switch
                {
                    ActorKind.Admin => true,
                    ActorKind.TeacherOwner or ActorKind.HeadOwner => true,
                    ActorKind.TeacherPeerSameDept => status == "Approved",
                    ActorKind.HeadPeerSameDept => status is "Approved" or "Submitted",
                    _ => false
                };
                yield return [kind, status, expected];
            }
    }

    /// <summary>Verify QuestionRules.CanRead matches section 3.2 for every actor role and question status.</summary>
    [Theory]
    [MemberData(nameof(QuestionVisibilityCases))]
    public void Question_visibility_matrix_matches_spec(ActorKind kind, string status, bool expected)
    {
        Assert.Equal(expected, QuestionRules.CanRead(Actor(kind), NewQuestion(status)));
    }

    public static IEnumerable<object[]> QuestionTransitionAuthorityCases()
    {
        foreach (var kind in Enum.GetValues<ActorKind>())
            foreach (var action in Enum.GetValues<QuestionAction>())
            {
                var initialStatuses = action switch
                {
                    QuestionAction.Submit or QuestionAction.MarkReviewed => new[] { "Draft", "Rejected" },
                    QuestionAction.Withdraw or QuestionAction.Approve or QuestionAction.Reject => new[] { "Submitted" },
                    QuestionAction.Archive => new[] { "Approved" },
                    _ => new[] { "Draft" }
                };

                var expectedError = action switch
                {
                    QuestionAction.Submit or QuestionAction.Withdraw or QuestionAction.MarkReviewed =>
                        kind is ActorKind.TeacherOwner or ActorKind.HeadOwner ? null : "Forbidden",
                    QuestionAction.Approve or QuestionAction.Reject => kind switch
                    {
                        ActorKind.HeadOwner => "SelfReview",
                        ActorKind.HeadPeerSameDept or ActorKind.Admin => null,
                        _ => "Forbidden"
                    },
                    QuestionAction.Archive =>
                        kind is ActorKind.TeacherOwner or ActorKind.HeadOwner or ActorKind.HeadPeerSameDept or ActorKind.Admin
                            ? null
                            : "Forbidden",
                    _ => "Forbidden"
                };

                foreach (var initialStatus in initialStatuses)
                    yield return [kind, action, initialStatus, expectedError!];
            }
    }

    /// <summary>Verify QuestionRules.Transition enforces the role, department, ownership, and self-review matrix across valid source states.</summary>
    [Theory]
    [MemberData(nameof(QuestionTransitionAuthorityCases))]
    public void Question_transition_permission_matrix_matches_spec(ActorKind kind, QuestionAction action, string initialStatus, string? expectedError)
    {
        var question = NewQuestion(initialStatus);
        if (action == QuestionAction.MarkReviewed)
        {
            question.Source = "AI";
            question.AiReviewedByAuthor = false;
        }

        if (expectedError is null)
        {
            var changed = QuestionRules.Transition(Actor(kind), question, action, "Valid review note", DateTime.UtcNow);
            Assert.True(changed);
        }
        else
        {
            var ex = Assert.Throws<QuestionException>(() =>
                QuestionRules.Transition(Actor(kind), question, action, "Valid review note", DateTime.UtcNow));
            Assert.Equal(expectedError, ex.Code);
            Assert.Equal(initialStatus, question.Status);
        }
    }

    public static IEnumerable<object[]> ExamVisibilityCases()
    {
        foreach (var kind in Enum.GetValues<ActorKind>())
            foreach (var status in ExamRules.States)
            {
                var canRead = kind switch
                {
                    ActorKind.Admin => true,
                    ActorKind.TeacherOwner or ActorKind.HeadOwner => true,
                    ActorKind.HeadPeerSameDept => true,
                    ActorKind.TeacherPeerSameDept => status == "Published",
                    _ => false
                };
                var canReadAnswers = kind is ActorKind.Admin or ActorKind.TeacherOwner or ActorKind.HeadOwner or ActorKind.HeadPeerSameDept;
                yield return [kind, status, canRead, canReadAnswers];
            }
    }

    /// <summary>Verify ExamRules.CanRead and CanReadAnswers match section 3.2, keeping answers hidden from peer teachers.</summary>
    [Theory]
    [MemberData(nameof(ExamVisibilityCases))]
    public void Exam_visibility_and_answer_matrix_matches_spec(ActorKind kind, string status, bool expectedRead, bool expectedAnswers)
    {
        var actor = Actor(kind);
        var exam = NewExam();
        Assert.Equal(expectedRead, ExamRules.CanRead(actor, exam, status));
        Assert.Equal(expectedAnswers, ExamRules.CanReadAnswers(actor, exam));
    }

    public static IEnumerable<object[]> ExamTransitionAuthorityCases()
    {
        foreach (var kind in Enum.GetValues<ActorKind>())
            foreach (var action in Enum.GetValues<ExamAction>())
            {
                var sourceStatuses = action switch
                {
                    ExamAction.Submit => new[] { "Draft", "Rejected" },
                    ExamAction.Withdraw or ExamAction.Approve or ExamAction.Reject => new[] { "Submitted" },
                    ExamAction.Archive => new[] { "Approved", "Rejected" },
                    ExamAction.Publish => new[] { "Approved" },
                    ExamAction.Unpublish => new[] { "Published" },
                    _ => new[] { "Draft" }
                };

                var expectedError = action switch
                {
                    ExamAction.Submit or ExamAction.Withdraw =>
                        kind is ActorKind.TeacherOwner or ActorKind.HeadOwner ? null : "Forbidden",
                    ExamAction.Approve or ExamAction.Reject => kind switch
                    {
                        ActorKind.HeadOwner => "SelfReview",
                        ActorKind.HeadPeerSameDept or ActorKind.Admin => null,
                        _ => "Forbidden"
                    },
                    ExamAction.Archive =>
                        kind is ActorKind.TeacherOwner or ActorKind.HeadOwner or ActorKind.HeadPeerSameDept or ActorKind.Admin
                            ? null
                            : "Forbidden",
                    ExamAction.Publish or ExamAction.Unpublish =>
                        kind == ActorKind.Admin ? null : "Forbidden",
                    _ => "Forbidden"
                };

                foreach (var sourceStatus in sourceStatuses)
                    yield return [kind, action, sourceStatus, expectedError!];
            }
    }

    /// <summary>Verify ExamRules.RequireAction enforces role, ownership, department, activation, and self-review rules across all valid source states.</summary>
    [Theory]
    [MemberData(nameof(ExamTransitionAuthorityCases))]
    public void Exam_transition_permission_matrix_matches_spec(ActorKind kind, ExamAction action, string sourceStatus, string? expectedError)
    {
        var version = new ExamVersion { Status = sourceStatus, SubmittedAt = DateTime.UnixEpoch, UpdatedAt = DateTime.UnixEpoch };

        if (expectedError is null)
        {
            ExamRules.RequireAction(Actor(kind), NewExam(), version, action);
        }
        else
        {
            var ex = Assert.Throws<ExamException>(() => ExamRules.RequireAction(Actor(kind), NewExam(), version, action));
            Assert.Equal(expectedError, ex.Code);
        }
    }
}
