using System.Text;
using System.Text.Json;
using ExamMaker.Application.Ai.Verification;

namespace ExamMaker.Application.Tests.Ai.Verification;

public sealed class AiAnswerComparerTests
{
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AiVerificationOutcome Compare(string type, JsonElement payload, string modelAnswerJson) =>
        AiAnswerComparer.Compare(type, payload, Json(modelAnswerJson));

    private static JsonElement Mc(string correct) => Json($$"""
        {"options":[{"label":"A","text":"a"},{"label":"B","text":"b"},{"label":"C","text":"c"},{"label":"D","text":"d"}],"correctLabel":"{{correct}}"}
        """);

    private static JsonElement Tf(bool a, bool b, bool c, bool d) => Json($$"""
        {"statements":[{"label":"a","text":"x","isTrue":{{Lower(a)}}},{"label":"b","text":"x","isTrue":{{Lower(b)}}},{"label":"c","text":"x","isTrue":{{Lower(c)}}},{"label":"d","text":"x","isTrue":{{Lower(d)}}}]}
        """);

    private static string Lower(bool value) => value ? "true" : "false";

    private static JsonElement Text(string accepted) =>
        Json($$"""{"answerType":"text","acceptedAnswers":[{{JsonSerializer.Serialize(accepted)}}]}""");

    private static JsonElement Number(string acceptedJsonArray, string? tolerance) =>
        Json($$"""{"answerType":"number","acceptedAnswers":{{acceptedJsonArray}}{{(tolerance is null ? "" : $",\"tolerance\":{tolerance}")}}}""");

    [Fact]
    public void Mc_same_label_agrees() =>
        Assert.Equal(new AiVerificationOutcome("Agree", null), Compare("MultipleChoice", Mc("B"), "\"B\""));

    [Fact]
    public void Mc_other_label_disagrees_with_note() =>
        Assert.Equal(new AiVerificationOutcome("Disagree", "Mô hình tự giải chọn C, đáp án gốc là B."), Compare("MultipleChoice", Mc("B"), "\"C\""));

    [Fact]
    public void Tf_all_match_agrees() =>
        Assert.Equal("Agree", Compare("TrueFalse", Tf(true, false, true, false), """{"a":true,"b":false,"c":true,"d":false}""").Verification);

    [Fact]
    public void Tf_mismatch_lists_labels() =>
        Assert.Equal("Mô hình tự giải khác đáp án gốc ở ý: b, d.",
            Compare("TrueFalse", Tf(true, false, true, false), """{"a":true,"b":true,"c":true,"d":true}""").Note);

    [Theory]
    [InlineData("12,5", """["12,5"]""", null, "Agree")]
    [InlineData("12.5", """["12,5"]""", null, "Agree")]
    [InlineData("12.51", """["12,5"]""", "0.01", "Agree")]
    [InlineData("12.52", """["12,5"]""", "0.01", "Disagree")]
    [InlineData("13", """["12","13"]""", null, "Agree")]
    [InlineData("12 m/s", """["12"]""", null, "Disagree")]
    [InlineData("99", """["12"]""", "1e500", "Agree")]
    [InlineData("12", """["12"]""", "1e-500", "Agree")]
    [InlineData("13", """["12"]""", "1e-500", "Disagree")]
    [InlineData("12", """["12"]""", "null", "Agree")]
    // Exact arithmetic: decimal would round 1e-29 to 0 and cannot hold 1e30.
    [InlineData("0.00000000000000000000000000001", """["0"]""", null, "Disagree")]
    [InlineData("0", """["1000000000000000000000000000000"]""", "1e31", "Agree")]
    [InlineData("0", """["1000000000000000000000000000000"]""", "1e29", "Disagree")]
    [InlineData("-3,5", """["-3.5"]""", null, "Agree")]
    [InlineData("12.50", """["12,5"]""", null, "Agree")]
    [InlineData("1e1", """["10"]""", null, "Disagree")]
    // Exponents far outside the validator range (+-1000) are ignored (tolerance 0) instead of overflowing.
    [InlineData("13", """["12"]""", "1e-2147483648", "Disagree")]
    [InlineData("13", """["12"]""", "1e2147483647", "Disagree")]
    [InlineData("13", """["12"]""", "1e10001", "Disagree")]
    [InlineData("0", """["0"]""", "0", "Agree")]
    public void Number_answers(string model, string accepted, string? tolerance, string expected)
    {
        var outcome = Compare("ShortAnswer", Number(accepted, tolerance), JsonSerializer.Serialize(model));
        Assert.Equal(expected, outcome.Verification);
    }

    [Fact]
    public void Text_answer_matches_after_normalization() =>
        Assert.Equal("Agree", Compare("ShortAnswer", Text("Quang hợp"), "\"  quang   HỢP \"").Verification);

    [Fact]
    public void Short_answer_disagree_note_truncates_model_value_to_100_chars() =>
        Assert.Equal($"Mô hình tự giải ra \"{new string('x', 100)}\", không khớp đáp án chấp nhận.",
            Compare("ShortAnswer", Text("y"), $"\"{new string('x', 150)}\"").Note);

    [Fact]
    public void Short_answer_note_replaces_control_characters_with_space() =>
        Assert.Equal("Mô hình tự giải ra \"a b\", không khớp đáp án chấp nhận.",
            Compare("ShortAnswer", Text("y"), "\"a\\u0000b\"").Note);

    [Fact]
    public void Short_answer_note_drops_unpaired_surrogates()
    {
        var note = AiVerificationNotes.ShortAnswer("x\ud800y");

        Assert.Equal("Mô hình tự giải ra \"xy\", không khớp đáp án chấp nhận.", note);
        Assert.Equal(note, new UTF8Encoding(false, true).GetString(new UTF8Encoding(false, true).GetBytes(note)));
    }

    [Fact]
    public void Short_answer_note_does_not_split_surrogate_pair_at_limit() =>
        Assert.Equal($"Mô hình tự giải ra \"{new string('x', 99)}\", không khớp đáp án chấp nhận.",
            Compare("ShortAnswer", Text("y"), $"\"{new string('x', 99)}\ud83d\ude00\"").Note);

    [Fact]
    public void Number_matches_exact_string_beyond_decimal_range() =>
        Assert.Equal("Agree", Compare("ShortAnswer", Number("""["1234567890123456789012345678901234"]""", null),
            "\"1234567890123456789012345678901234\"").Verification);

    [Fact]
    public void Unsupported_type_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Compare("Essay", Text("y"), "\"y\""));
}
