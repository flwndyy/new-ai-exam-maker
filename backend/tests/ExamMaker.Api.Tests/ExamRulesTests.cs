using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Exams;
using ExamMaker.Domain.Exams;

namespace ExamMaker.Api.Tests;

public sealed class ExamRulesTests
{
    private static readonly Guid Owner = Guid.NewGuid(), Department = Guid.NewGuid();
    private static AuthenticatedUser Actor(string role, bool own = false, bool same = true) =>
        new(own ? Owner : Guid.NewGuid(), "synthetic", "test@example.invalid", "Synthetic", role, same ? Department : Guid.NewGuid(), null, true);
    private static Exam Exam() => new() { Id = Guid.NewGuid(), CreatedBy = Owner, DepartmentId = Department };

    public static IEnumerable<object[]> StateCases()
    {
        foreach (var state in ExamRules.States)
            foreach (var action in Enum.GetValues<ExamAction>())
                yield return [state, action, action switch
                {
                    ExamAction.Submit => state is "Draft" or "Rejected",
                    ExamAction.Withdraw or ExamAction.Approve or ExamAction.Reject => state == "Submitted",
                    ExamAction.Archive => state is "Approved" or "Rejected",
                    ExamAction.Publish => state == "Approved",
                    ExamAction.Unpublish => state == "Published",
                    _ => false
                }];
    }

    [Theory]
    [MemberData(nameof(StateCases))]
    // Cover every state/action pair so terminal and frozen editions cannot silently become editable.
    public void State_table_rejects_every_unsupported_transition(string state, ExamAction action, bool allowed)
    {
        var actor = action is ExamAction.Approve or ExamAction.Reject ? Actor("head")
            : action is ExamAction.Publish or ExamAction.Unpublish ? Actor("admin") : Actor("teacher", true);
        var version = new ExamVersion { Status = state, UpdatedAt = DateTime.UnixEpoch, SubmittedAt = DateTime.UnixEpoch };
        if (!allowed)
        {
            Assert.Equal("InvalidStateTransition", Assert.Throws<ExamException>(() =>
                ExamRules.Transition(actor, Exam(), version, action, new(DateTime.UnixEpoch, "Reason"), DateTime.UtcNow)).Code);
            Assert.Equal(state, version.Status);
        }
        else
        {
            ExamRules.Transition(actor, Exam(), version, action, new(DateTime.UnixEpoch, "Reason"), DateTime.UtcNow);
            Assert.NotEqual(state, version.Status);
            Assert.True(version.UpdatedAt > DateTime.UnixEpoch);
            Assert.Equal(0, version.UpdatedAt.Ticks % TimeSpan.TicksPerMicrosecond);
        }
    }

    [Theory]
    [InlineData("teacher", true, true, "Forbidden")]
    [InlineData("head", true, true, "SelfReview")]
    [InlineData("head", false, false, "Forbidden")]
    [InlineData("head", false, true, null)]
    [InlineData("admin", true, false, "SelfReview")]
    [InlineData("admin", false, false, null)]
    public void Reviewer_authority_is_checked_before_the_review_token(string role, bool own, bool same, string? error)
    {
        foreach (var action in new[] { ExamAction.Approve, ExamAction.Reject })
        {
            var version = new ExamVersion { Status = "Submitted" };
            var result = Assert.Throws<ExamException>(() => ExamRules.Transition(Actor(role, own, same), Exam(), version, action, null, DateTime.UtcNow));
            Assert.Equal(error ?? "ValidationFailed", result.Code);
            if (error is null) Assert.Equal("submittedAt", result.Field);
        }
    }

    [Theory]
    [InlineData("teacher", false, true, "Draft", false, false)]
    [InlineData("teacher", false, true, "Published", true, false)]
    [InlineData("teacher", true, false, "Draft", true, true)]
    [InlineData("head", false, true, "Draft", true, true)]
    [InlineData("head", false, false, "Published", false, false)]
    [InlineData("admin", false, false, "Draft", true, true)]
    public void Metadata_visibility_and_answer_permissions_are_separate(string role, bool own, bool same, string state, bool visible, bool answers)
    {
        var actor = Actor(role, own, same); var exam = Exam();
        Assert.Equal(visible, ExamRules.CanRead(actor, exam, state));
        Assert.Equal(answers, ExamRules.CanReadAnswers(actor, exam));
    }

    [Fact]
    public void Review_tokens_survive_javascript_but_reject_a_previous_submission()
    {
        var version = new ExamVersion { SubmittedAt = DateTime.UnixEpoch.AddTicks(1234560), Status = "Submitted", UpdatedAt = DateTime.UnixEpoch };
        var token = DateTimeOffset.FromUnixTimeMilliseconds(new DateTimeOffset(version.SubmittedAt.Value).ToUnixTimeMilliseconds());
        ExamRules.Transition(Actor("head"), Exam(), version, ExamAction.Reject, new(token, "Reason"), DateTime.UnixEpoch);
        ExamRules.Transition(Actor("teacher", true), Exam(), version, ExamAction.Submit, null, DateTime.UnixEpoch.AddSeconds(1));
        Assert.Equal("Conflict", Assert.Throws<ExamException>(() =>
            ExamRules.Transition(Actor("head"), Exam(), version, ExamAction.Approve, new(token), DateTime.UtcNow)).Code);
        var first = version.SubmittedAt;
        ExamRules.Transition(Actor("teacher", true), Exam(), version, ExamAction.Withdraw, null, DateTime.UnixEpoch);
        ExamRules.Transition(Actor("teacher", true), Exam(), version, ExamAction.Submit, null, DateTime.UnixEpoch);
        Assert.True(version.SubmittedAt >= first!.Value.AddMilliseconds(1));
    }

    [Fact]
    public void Hash_is_stable_across_property_order_and_decimal_scale_but_changes_with_content()
    {
        var source = Guid.NewGuid();
        ExamVersion Version(string payload, decimal points = 1m) => new()
        {
            DurationMinutes = 45,
            Questions = [new() { SourceQuestionId = source, SourceRevision = 1, SortOrder = 1,
                Points = points, Type = "Essay", Difficulty = "Recognition", StemSnapshot = "Synthetic", PayloadSnapshot = payload }]
        };
        var hash = ExamRules.ContentHash(Version("{\"b\":2,\"a\":{\"y\":1,\"x\":0}}"));
        Assert.Equal(hash, ExamRules.ContentHash(Version("{\"a\":{\"x\":0,\"y\":1},\"b\":2}", 1.00m)));
        Assert.NotEqual(hash, ExamRules.ContentHash(Version("{\"a\":1}", 2)));
        var duration = Version("{\"b\":2,\"a\":{\"y\":1,\"x\":0}}"); duration.DurationMinutes = 60;
        Assert.NotEqual(hash, ExamRules.ContentHash(duration)); Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Composition_validation_bounds_input_and_detects_duplicate_sources_and_overflow()
    {
        var id = Guid.NewGuid();
        Assert.Equal(0, ExamRules.ValidateComposition(new(45, [])));
        Assert.Equal("items", Assert.Throws<ExamException>(() => ExamRules.ValidateComposition(new(45, null))).Field);
        Assert.Throws<ExamException>(() => ExamRules.ValidateComposition(new(0, [])));
        Assert.Throws<ExamException>(() => ExamRules.ValidateComposition(new(45, [new(id, 1), new(id, 1)])));
        Assert.Throws<ExamException>(() => ExamRules.ValidateComposition(new(45, [new(id, 0)])));
        Assert.Throws<ExamException>(() => ExamRules.ValidateComposition(new(45, [new(id, decimal.MaxValue), new(Guid.NewGuid(), 1)])));
        Assert.Throws<ExamException>(() => ExamRules.ValidateComposition(new(45, Enumerable.Range(0, 201).Select(_ => new ExamItemInput(Guid.NewGuid(), 1)).ToArray())));
    }
}
