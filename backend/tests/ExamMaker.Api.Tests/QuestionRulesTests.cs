using System.Text.Json.Nodes;
using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Questions;
using ExamMaker.Domain.Questions;

namespace ExamMaker.Api.Tests;

public sealed class QuestionRulesTests
{
    [Theory]
    [InlineData("1e-20000", false)]
    [InlineData("1e200000", false)]
    [InlineData("1.2345678901234567890123456789e-1000", false)]
    [InlineData("1.2345678901234567890123456789e-990", true)]
    [InlineData("1e-1000", true)]
    [InlineData("-1e-1000", false)]
    // Points and tolerance must apply the same storage budget to the expanded decimal, without decimal underflow.
    public void Numeric_storage_rules_are_shared_across_payload_fields(string number, bool allowed)
    {
        foreach (var type in new[] { "Essay", "ShortAnswer" })
        {
            var json = type == "Essay"
                ? "{\"modelAnswer\":\"\",\"rubric\":[{\"criterion\":\"Synthetic\",\"points\":" + number + "}]}"
                : "{\"answerType\":\"number\",\"acceptedAnswers\":[\"1\"],\"tolerance\":" + number + "}";
            var payload = JsonSerializer.Deserialize<JsonElement>(json);
            if (allowed) QuestionPayloadRules.Validate(type, payload);
            else Assert.Equal("payload", Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate(type, payload)).Field);
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0")]
    [InlineData("0e-1000")]
    public void Tolerance_accepts_zero_without_weakening_positive_rubric_rules(string number)
    {
        QuestionPayloadRules.Validate("ShortAnswer", JsonSerializer.Deserialize<JsonElement>(
            "{\"answerType\":\"number\",\"acceptedAnswers\":[\"1\"],\"tolerance\":" + number + "}"));
        Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate("Essay", JsonSerializer.Deserialize<JsonElement>(
            "{\"modelAnswer\":\"\",\"rubric\":[{\"criterion\":\"Synthetic\",\"points\":" + number + "}]}")));
    }

    [Fact]
    // Millisecond normalization must never reuse a token when several workflow operations see the same clock value.
    public void Resubmission_tokens_advance_even_with_a_frozen_clock()
    {
        var q = Question(); var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        q.UpdatedAt = clock;
        var actor = Actor("teacher", true);
        QuestionRules.Transition(actor, q, QuestionAction.Submit, null, clock);
        var first = q.SubmittedAt!.Value;
        QuestionRules.Transition(actor, q, QuestionAction.Withdraw, null, clock);
        QuestionRules.Edit(actor, q, new(q.SubjectId, q.TopicId, q.Grade, q.Type, q.Difficulty, "Replacement",
            JsonSerializer.Deserialize<JsonElement>(q.Payload)), clock);
        QuestionRules.Transition(actor, q, QuestionAction.Submit, null, clock);
        Assert.True(q.SubmittedAt >= first.AddMilliseconds(1));
        Assert.Equal(0, q.SubmittedAt!.Value.Ticks % TimeSpan.TicksPerMillisecond);
    }

    [Theory]
    [InlineData("1e-30", true)]
    [InlineData("1e-1000", true)]
    [InlineData("1e30", true)]
    [InlineData("0.000000000000000000000000000001", true)]
    [InlineData("-1e-30", false)]
    [InlineData("0e100", false)]
    [InlineData("0.000e-20", false)]
    [InlineData("-0e20", false)]
    [InlineData("1e-20000", false)]
    [InlineData("1e200000", false)]
    [InlineData("1e1001", false)]
    [InlineData("1e-1001", false)]
    [InlineData("1e-2147483649", false)]
    // Positive rubric numbers remain valid below decimal precision; exponent digits cannot make zero positive.
    public void Essay_rubric_positivity_uses_the_original_json_number(string number, bool allowed)
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("{\"modelAnswer\":\"\",\"rubric\":[{\"criterion\":\"Synthetic\",\"points\":" + number + "}]}");
        if (allowed) QuestionPayloadRules.Validate("Essay", payload);
        else Assert.Equal("payload", Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate("Essay", payload)).Field);
    }

    public const string ValidPayload = """{"options":[{"label":"A","text":"One"},{"label":"B","text":"Two"},{"label":"C","text":"Three"},{"label":"D","text":"Four"}],"correctLabel":"A"}""";
    [Fact]
    // Lexical numeric precision must not exceed the safe jsonb storage budget even without scientific notation.
    public void Essay_rejects_oversized_numeric_tokens()
    {
        var json = "{\"modelAnswer\":\"\",\"rubric\":[{\"criterion\":\"Synthetic\",\"points\":1" + new string('0', 1024) + "}]}";
        Assert.Equal("payload", Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate("Essay",
            JsonSerializer.Deserialize<JsonElement>(json))).Field);
    }

    [Theory]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    // Whitespace outside a valid normalized explanation must not count against the stored-text limit.
    public void Explanation_length_is_checked_after_normalization(int length, bool valid)
    {
        var input = new QuestionInput(Guid.NewGuid(), Guid.NewGuid(), 10, "MultipleChoice", "Recognition", "Synthetic",
            JsonSerializer.Deserialize<JsonElement>(ValidPayload), "  " + new string('x', length) + "\n  ");
        if (valid) Assert.Equal(new string('x', length), QuestionRules.Validate(input).Explanation);
        else Assert.Equal("explanation", Assert.Throws<QuestionException>(() => QuestionRules.Validate(input)).Field);
    }

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
        q.AiReviewedByAuthor = action != QuestionAction.MarkReviewed;
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
        if (action == QuestionAction.Submit)
        {
            Assert.Equal(0, q.SubmittedAt!.Value.Ticks % TimeSpan.TicksPerMillisecond);
            Assert.Equal(q.SubmittedAt, q.UpdatedAt);
        }
        else Assert.Equal(now, q.UpdatedAt);
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
    [InlineData("head", true, false, true)]
    [InlineData("teacher", true, false, true)]
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
    [Fact]
    // Vietnamese decimal notation must match the context example without rewriting accepted answer strings.
    public void Numeric_short_answers_accept_both_decimal_separators()
    {
        var payload = JsonSerializer.Deserialize<JsonElement>("""{"answerType":"number","acceptedAnswers":["12","12,0","12.0"],"tolerance":0}""");
        QuestionPayloadRules.Validate("ShortAnswer", payload);
        Assert.Equal("12,0", payload.GetProperty("acceptedAnswers")[1].GetString());
        foreach (var value in new[] { "NaN", "Infinity", "12,0.1", "12,,0", " 12 ", "+12", "1e3", "12\n" })
            Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate("ShortAnswer",
                JsonSerializer.SerializeToElement(new { answerType = "number", acceptedAnswers = new[] { value } })));
    }

    [Fact]
    // Formatting-only edits of approved worker output must preserve its revision and human/AI verification flags.
    public void Noop_edit_compares_normalized_stem_and_optional_explanation()
    {
        var q = Question("Approved");
        q.Source = "AI"; q.AiReviewedByAuthor = true; q.AiVerification = "Agree";
        q.Stem += "  "; q.Explanation = " \t "; q.ReviewNote = "OK";
        var input = new QuestionInput(q.SubjectId, q.TopicId, q.Grade, q.Type, q.Difficulty,
            q.Stem.Trim(), JsonSerializer.Deserialize<JsonElement>(q.Payload), "");
        Assert.False(QuestionRules.Edit(Actor("teacher", true), q, input, DateTime.UtcNow));
        Assert.Equal("Approved", q.Status); Assert.Equal(1, q.Revision);
        Assert.True(q.AiReviewedByAuthor); Assert.Equal("Agree", q.AiVerification); Assert.Equal("OK", q.ReviewNote);
        Assert.True(QuestionRules.Edit(Actor("teacher", true), q, input with { Explanation = " Changed " }, DateTime.UtcNow));
        Assert.Equal("Changed", q.Explanation); Assert.Equal(q.Stem.Trim(), q.Stem); Assert.Equal(2, q.Revision);
    }

    [Theory]
    [InlineData("MultipleChoice", "options/0/text")]
    [InlineData("TrueFalse", "statements/0/text")]
    [InlineData("ShortAnswer", "acceptedAnswers/0")]
    [InlineData("ShortAnswer", "unit")]
    [InlineData("Essay", "modelAnswer")]
    [InlineData("Essay", "rubric/0/criterion")]
    // Check the exact text boundary independently for each schema field, not just one shared helper path.
    public void Payload_text_fields_have_bounded_lengths(string type, string path)
    {
        var json = type switch
        {
            "MultipleChoice" => ValidPayload,
            "TrueFalse" => """{"statements":[{"label":"a","text":"A","isTrue":true},{"label":"b","text":"B","isTrue":false},{"label":"c","text":"C","isTrue":true},{"label":"d","text":"D","isTrue":false}]}""",
            "ShortAnswer" => """{"answerType":"text","acceptedAnswers":["Answer"],"unit":"unit"}""",
            _ => """{"modelAnswer":"Answer","rubric":[{"criterion":"Reason","points":1}]}"""
        };
        foreach (var length in new[] { 10000, 10001 })
        {
            var root = JsonNode.Parse(json)!;
            var parts = path.Split('/');
            var parent = root;
            foreach (var part in parts[..^1]) parent = parent is JsonArray ? parent[int.Parse(part)]! : parent[part]!;
            if (parent is JsonArray) parent[int.Parse(parts[^1])] = new string('x', length);
            else parent[parts[^1]] = new string('x', length);
            var payload = JsonSerializer.SerializeToElement(root);
            if (length == 10000) QuestionPayloadRules.Validate(type, payload);
            else Assert.Equal("payload", Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate(type, payload)).Field);
        }
    }

    [Theory]
    [InlineData("ShortAnswer")]
    [InlineData("Essay")]
    public void Payload_arrays_limit_answer_and_rubric_counts(string type)
    {
        foreach (var count in new[] { 20, 21 })
        {
            var payload = type == "ShortAnswer"
                ? JsonSerializer.SerializeToElement(new { answerType = "text", acceptedAnswers = Enumerable.Repeat("Answer", count) })
                : JsonSerializer.SerializeToElement(new { modelAnswer = "Answer", rubric = Enumerable.Range(0, count).Select(_ => new { criterion = "Reason", points = 1 }) });
            if (count == 20) QuestionPayloadRules.Validate(type, payload);
            else Assert.Equal("payload", Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate(type, payload)).Field);
        }
    }

    [Theory]
    [InlineData("options/0/text")]
    [InlineData("options/0/label")]
    [InlineData("correctLabel")]
    // JsonElement binding accepts raw escapes; every string extraction must convert a lone surrogate into a payload error.
    public void Unpaired_surrogate_values_are_payload_validation_failures(string path)
    {
        var json = path switch
        {
            "options/0/text" => ValidPayload.Replace("One", @"\ud800"),
            "options/0/label" => ValidPayload.Replace("\"A\",\"text\":\"One\"", "\"" + @"\ud800" + "\",\"text\":\"One\""),
            _ => ValidPayload.Replace("\"correctLabel\":\"A\"", "\"correctLabel\":\"" + @"\ud800" + "\"")
        };
        var error = Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate("MultipleChoice", JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Equal(400, error.StatusCode); Assert.Equal("payload", error.Field);
    }

    [Fact]
    public void Malformed_unicode_property_names_are_payload_errors()
    {
        var json = ValidPayload.Replace("correctLabel", @"\ud800");
        Assert.Equal("payload", Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate("MultipleChoice",
            JsonSerializer.Deserialize<JsonElement>(json))).Field);
    }

    [Theory]
    [InlineData("12")]
    [InlineData("-12")]
    [InlineData("-12,50")]
    [InlineData("0.5")]
    public void Finite_decimal_answer_grammar_allows_negative_numbers_and_both_separators(string answer) =>
        QuestionPayloadRules.Validate("ShortAnswer", JsonSerializer.SerializeToElement(new { answerType = "number", acceptedAnswers = new[] { answer } }));

    [Theory]
    [InlineData("MultipleChoice")]
    [InlineData("TrueFalse")]
    // Reject valid-but-shuffled label sets instead of persisting an unexpected print order.
    public void Option_labels_must_follow_the_canonical_display_order(string type)
    {
        var payload = type == "MultipleChoice"
            ? JsonNode.Parse(ValidPayload)!
            : JsonNode.Parse("""{"statements":[{"label":"a","text":"A","isTrue":true},{"label":"b","text":"B","isTrue":false},{"label":"c","text":"C","isTrue":true},{"label":"d","text":"D","isTrue":false}]}""")!;
        var name = type == "MultipleChoice" ? "options" : "statements";
        var array = payload[name]!.AsArray();
        var first = array[0]!.DeepClone(); array[0] = array[3]!.DeepClone(); array[3] = first;
        Assert.Equal("payload", Assert.Throws<QuestionException>(() => QuestionPayloadRules.Validate(type,
            JsonSerializer.SerializeToElement(payload))).Field);
    }

    [Fact]
    // A retry is still authorized and state-checked, but must not look like another review operation.
    public void Mark_reviewed_is_idempotent_without_changing_updated_at()
    {
        var q = Question(); q.Source = "AI";
        var first = DateTime.UtcNow;
        Assert.True(QuestionRules.Transition(Actor("teacher", true), q, QuestionAction.MarkReviewed, null, first));
        Assert.False(QuestionRules.Transition(Actor("teacher", true), q, QuestionAction.MarkReviewed, null, first.AddMinutes(1)));
        Assert.Equal(first, q.UpdatedAt);
    }

}
