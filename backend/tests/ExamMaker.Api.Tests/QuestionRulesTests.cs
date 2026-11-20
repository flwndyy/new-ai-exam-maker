using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Questions;
using ExamMaker.Domain.Questions;

namespace ExamMaker.Api.Tests;

public sealed class QuestionRulesTests
{
    public const string ValidPayload = """{"options":[{"label":"A","text":"One"},{"label":"B","text":"Two"},{"label":"C","text":"Three"},{"label":"D","text":"Four"}],"correctLabel":"A"}""";
    private static readonly Guid Owner = Guid.NewGuid();
    private static readonly Guid Department = Guid.NewGuid();
    private static AuthenticatedUser Actor(string role, bool own = false, bool sameDepartment = true) =>
        new(own ? Owner : Guid.NewGuid(), "synthetic", "test@example.invalid", "Synthetic", role,
            sameDepartment ? Department : Guid.NewGuid(), null, true);
    private static Question Question(string status = "Draft") => new()
    {
        Id = Guid.NewGuid(),
        CreatedBy = Owner,
        DepartmentId = Department,
        SubjectId = Guid.NewGuid(),
        TopicId = Guid.NewGuid(),
        Grade = 10,
        Type = "MultipleChoice",
        Difficulty = "Recognition",
        Stem = "Synthetic question",
        Payload = ValidPayload,
        Status = status
    };

    public static IEnumerable<object[]> StateCases()
    {
        foreach (var state in new[] { "Draft", "Submitted", "Approved", "Rejected", "Archived" })
            foreach (var action in Enum.GetValues<QuestionAction>())
            {
                var valid = action switch
                {
                    QuestionAction.Submit or QuestionAction.MarkReviewed => state is "Draft" or "Rejected",
                    QuestionAction.Withdraw or QuestionAction.Approve or QuestionAction.Reject => state == "Submitted",
                    QuestionAction.Archive => state == "Approved",
                    _ => false
                };
                yield return [state, action, valid];
            }
    }

    [Theory]
    [MemberData(nameof(StateCases))]
    // Exercise every action/state pair, including all attempted restorations of archived questions.
    public void All_workflow_state_pairs_follow_the_transition_table(string state, QuestionAction action, bool valid)
    {
        var q = Question(state);
        q.Source = "AI";
        q.AiReviewedByAuthor = true;
        var actor = action is QuestionAction.Approve or QuestionAction.Reject ? Actor("head") : Actor("teacher", true);
        var now = DateTime.UtcNow;
        if (!valid)
        {
            Assert.Equal("InvalidStateTransition", Assert.Throws<QuestionException>(() => QuestionRules.Transition(actor, q, action, "Reason", now)).Code);
            Assert.Equal(state, q.Status);
            return;
        }
        QuestionRules.Transition(actor, q, action, "Reason", now);
        Assert.Equal(action switch
        {
            QuestionAction.Submit => "Submitted",
            QuestionAction.Withdraw => "Draft",
            QuestionAction.Approve => "Approved",
            QuestionAction.Reject => "Rejected",
            QuestionAction.Archive => "Archived",
            _ => state
        }, q.Status);
        Assert.Equal(now, q.UpdatedAt);
    }

    [Theory]
    [InlineData("teacher", false, true, "Forbidden")]
    [InlineData("teacher", true, true, "Forbidden")]
    [InlineData("head", false, true, null)]
    [InlineData("head", false, false, "Forbidden")]
    [InlineData("head", true, true, "SelfReview")]
    [InlineData("admin", false, false, null)]
    [InlineData("admin", true, true, "SelfReview")]
    // Both approval and rejection must distinguish self-review from role/department denials.
    public void Reviewer_matrix_applies_to_both_decisions(string role, bool own, bool same, string? error)
    {
        foreach (var action in new[] { QuestionAction.Approve, QuestionAction.Reject })
        {
            var q = Question("Submitted");
            var actor = Actor(role, own, same);
            if (error is null) QuestionRules.Transition(actor, q, action, "Reason", DateTime.UtcNow);
            else Assert.Equal(error, Assert.Throws<QuestionException>(() => QuestionRules.Transition(actor, q, action, "Reason", DateTime.UtcNow)).Code);
        }
    }

    [Theory]
    [InlineData("teacher", true, true, true)]
    [InlineData("teacher", false, true, false)]
    [InlineData("head", false, true, true)]
    [InlineData("head", false, false, false)]
    [InlineData("head", true, false, false)]
    [InlineData("admin", false, false, true)]
    [InlineData("admin", true, false, true)]
    public void Archive_obeys_the_separate_role_and_department_matrix(string role, bool own, bool same, bool allowed)
    {
        var q = Question("Approved");
        var actor = Actor(role, own, same);
        if (allowed) QuestionRules.Transition(actor, q, QuestionAction.Archive, null, DateTime.UtcNow);
        else Assert.Equal("Forbidden", Assert.Throws<QuestionException>(() => QuestionRules.Transition(actor, q, QuestionAction.Archive, null, DateTime.UtcNow)).Code);
    }

    public static IEnumerable<object[]> VisibilityCases()
    {
        foreach (var role in new[] { "teacher", "head", "admin" })
            foreach (var state in new[] { "Draft", "Rejected", "Submitted", "Approved", "Archived" })
                foreach (var own in new[] { false, true })
                    foreach (var same in new[] { false, true })
                        yield return [role, state, own, same,
                role == "admin" || own || same && (state == "Approved" || role == "head" && state == "Submitted")];
    }

    [Theory]
    [MemberData(nameof(VisibilityCases))]
    // Separate private drafts, approved shared content and submitted review visibility across all roles.
    public void Visibility_matches_the_role_department_and_ownership_matrix(string role, string state, bool own, bool same, bool allowed)
    {
        var actor = Actor(role, own, same);
        var q = Question(state);
        Assert.Equal(allowed, QuestionRules.CanRead(actor, q));
        Assert.False(QuestionRules.CanRead(actor with { IsActive = false }, q));
    }

    [Fact]
    public void Ai_submit_requires_review_and_content_changes_reset_verification()
    {
        var q = Question();
        q.Source = "AI";
        q.AiVerification = "Agree";
        q.AiVerificationNote = "Synthetic verification";
        var actor = Actor("teacher", true);
        Assert.Equal("AiReviewRequired", Assert.Throws<QuestionException>(() => QuestionRules.Transition(actor, q, QuestionAction.Submit, null, DateTime.UtcNow)).Code);
        QuestionRules.Transition(actor, q, QuestionAction.MarkReviewed, null, DateTime.UtcNow);
        QuestionRules.Transition(actor, q, QuestionAction.Submit, null, DateTime.UtcNow);
        QuestionRules.Transition(actor, q, QuestionAction.Withdraw, null, DateTime.UtcNow);
        var input = new QuestionInput(q.SubjectId, q.TopicId, 10, q.Type, q.Difficulty, "Changed", JsonSerializer.Deserialize<JsonElement>(ValidPayload));
        Assert.True(QuestionRules.Edit(actor, q, input, DateTime.UtcNow));
        Assert.False(q.AiReviewedByAuthor);
        Assert.Equal("NotChecked", q.AiVerification);
        Assert.Null(q.AiVerificationNote);
    }

    [Fact]
    public void Only_real_changes_reopen_an_approved_revision_and_clear_review_metadata()
    {
        var q = Question("Approved");
        q.ReviewedBy = Guid.NewGuid(); q.ReviewedAt = DateTime.UtcNow; q.ReviewNote = "Approved";
        var actor = Actor("teacher", true);
        var input = new QuestionInput(q.SubjectId, q.TopicId, 10, q.Type, q.Difficulty, q.Stem, JsonSerializer.Deserialize<JsonElement>(ValidPayload));
        Assert.False(QuestionRules.Edit(actor, q, input, DateTime.UtcNow));
        Assert.Equal("Approved", q.Status); Assert.Equal(1, q.Revision);
        Assert.True(QuestionRules.Edit(actor, q, input with { Stem = "Changed" }, DateTime.UtcNow));
        Assert.Equal("Draft", q.Status); Assert.Equal(2, q.Revision); Assert.Null(q.ReviewedBy); Assert.Null(q.ReviewNote);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\0note")]
    public void Reject_requires_a_valid_reason(string? note) =>
        Assert.Equal("ValidationFailed", Assert.Throws<QuestionException>(() => QuestionRules.Transition(Actor("head"), Question("Submitted"), QuestionAction.Reject, note, DateTime.UtcNow)).Code);

    [Theory]
    [InlineData("TrueFalse", """{"statements":[{"label":"a","text":"A","isTrue":true},{"label":"b","text":"B","isTrue":false},{"label":"c","text":"C","isTrue":true},{"label":"d","text":"D","isTrue":false}]}""")]
    [InlineData("ShortAnswer", """{"answerType":"number","acceptedAnswers":["1.5"],"tolerance":0.1,"unit":null}""")]
    [InlineData("ShortAnswer", """{"answerType":"text","acceptedAnswers":["Synthetic"]}""")]
    [InlineData("Essay", """{"modelAnswer":"","rubric":[{"criterion":"Reasoning","points":1}]}""")]
    public void All_supported_payload_shapes_are_validated(string type, string json) =>
        QuestionPayloadRules.Validate(type, JsonSerializer.Deserialize<JsonElement>(json));

    [Theory]
    [InlineData("MultipleChoice", "{}")]
    [InlineData("MultipleChoice", "null")]
    [InlineData("MultipleChoice", """{"options":[],"correctLabel":"A"}""")]
    [InlineData("MultipleChoice", """{"options":[],"correctLabel":"A","correctLabel":"B"}""")]
    [InlineData("TrueFalse", """{"statements":[{"label":"a","text":"A","isTrue":"true"}]}""")]
    [InlineData("ShortAnswer", """{"answerType":"text","acceptedAnswers":["One"],"tolerance":1}""")]
    [InlineData("ShortAnswer", """{"answerType":"number","acceptedAnswers":["NaN"]}""")]
    [InlineData("ShortAnswer", """{"answerType":"number","acceptedAnswers":["1"],"tolerance":"bad"}""")]
    [InlineData("ShortAnswer", """{"answerType":"text","acceptedAnswers":[]}""")]
    [InlineData("Essay", """{"modelAnswer":"","rubric":[{"criterion":"A","points":0}]}""")]
    [InlineData("Essay", """{"modelAnswer":"","rubric":[],"extra":true}""")]
    public void Invalid_or_mismatched_payloads_return_a_payload_validation_error(string type, string json)
    {
        var error = Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate(type, JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Equal(400, error.StatusCode); Assert.Equal("payload", error.Field);
    }
}
