using System.Text.Json;
using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Tests.Ai.Generation;

public sealed class AiOutputParserTests
{
    private const string Mc = """{"options":[{"label":"A","text":"1"},{"label":"B","text":"2"},{"label":"C","text":"3"},{"label":"D","text":"4"}],"correctLabel":"B"}""";
    private const string Tf = """{"statements":[{"label":"a","text":"x","isTrue":true},{"label":"b","text":"y","isTrue":false},{"label":"c","text":"z","isTrue":true},{"label":"d","text":"t","isTrue":false}]}""";
    private const string Sa = """{"answerType":"number","acceptedAnswers":["12,5","12.5"],"unit":"m/s","tolerance":0.01}""";
    private const string Es = """{"modelAnswer":"Bài giải mẫu","rubric":[{"criterion":"Lập luận","points":0.5},{"criterion":"Kết quả","points":0.5}]}""";

    private static AiGenerationSpec Spec(string type = "MultipleChoice", int count = 1) =>
        AiGenerationSpecs.Sample(type: type, count: count);

    private static string Question(string payload, string stem = "\"Câu hỏi $x^2$?\"", string explanation = "\"Vì vậy.\"") =>
        $$"""{"stem":{{stem}},"payload":{{payload}},"explanation":{{explanation}}}""";

    private static string Output(params string[] questions) => $$"""{"questions":[{{string.Join(",", questions)}}]}""";

    private static AiOutputException Invalid(string text, AiGenerationSpec spec) =>
        Assert.Throws<AiOutputException>(() => AiOutputParser.Parse(text, spec));

    [Fact]
    public void Valid_output_returns_questions_with_one_based_ordinals()
    {
        var questions = AiOutputParser.Parse(Output(Question(Mc), Question(Mc, stem: "\"Câu hai\"")), Spec(count: 2));

        Assert.Equal([1, 2], questions.Select(x => x.Ordinal));
        Assert.Equal("Câu hỏi $x^2$?", questions[0].Stem);
        Assert.Equal("Vì vậy.", questions[0].Explanation);
        Assert.Equal("B", questions[0].Payload.GetProperty("correctLabel").GetString());
    }

    [Fact]
    // The stored stem/explanation follow the same normalization as manual questions.
    public void Stem_is_trimmed_and_blank_explanation_becomes_null()
    {
        var question = AiOutputParser.Parse(Output(Question(Mc, stem: "\"  Câu  \"", explanation: "\"  \"")), Spec()).Single();

        Assert.Equal("Câu", question.Stem);
        Assert.Null(question.Explanation);
    }

    [Fact]
    public void Explanation_may_be_omitted_or_null()
    {
        var omitted = $$"""{"questions":[{"stem":"Câu","payload":{{Mc}}}]}""";
        Assert.Null(AiOutputParser.Parse(omitted, Spec()).Single().Explanation);
        Assert.Null(AiOutputParser.Parse(Output(Question(Mc, explanation: "null")), Spec()).Single().Explanation);
    }

    [Fact]
    // The payload must survive disposal of the parsed document.
    public void Payload_is_detached_from_the_parsed_document()
    {
        var question = AiOutputParser.Parse(Output(Question(Mc)), Spec()).Single();
        Assert.Equal(JsonValueKind.Object, question.Payload.ValueKind);
        Assert.Equal(4, question.Payload.GetProperty("options").GetArrayLength());
    }

    [Theory]
    [InlineData("TrueFalse", Tf)]
    [InlineData("ShortAnswer", Sa)]
    [InlineData("Essay", Es)]
    public void Each_question_type_accepts_its_documented_payload(string type, string payload) =>
        Assert.Single(AiOutputParser.Parse(Output(Question(payload)), Spec(type)));

    [Fact]
    public void Markdown_json_fence_is_stripped()
    {
        var fenced = "```json\n" + Output(Question(Mc)) + "\n```";
        Assert.Single(AiOutputParser.Parse(fenced, Spec()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Xin lỗi, tôi không thể")]
    [InlineData("{\"questions\":[")]
    [InlineData("{\"questions\":[]} trailing")]
    [InlineData("{\"questions\":[],}")]
    [InlineData("{/* c */\"questions\":[]}")]
    public void Malformed_json_is_invalid_json(string text) =>
        Assert.Equal(AiErrorCodes.InvalidJson, Invalid(text, Spec()).Code);

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"items\":[]}")]
    [InlineData("{\"questions\":{}}")]
    [InlineData("{\"questions\":[],\"extra\":1}")]
    [InlineData("{\"questions\":[],\"questions\":[]}")]
    [InlineData("{\"questions\":[],\"\\ud800\":1}")] // property name with an unpaired surrogate escape
    public void Wrong_root_shape_is_schema_violation(string text) =>
        Assert.Equal(AiErrorCodes.SchemaViolation, Invalid(text, Spec()).Code);

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    // Fewer or extra questions than requested would silently change what the teacher paid quota for.
    public void Question_count_must_match_request(int returned)
    {
        var error = Invalid(Output(Enumerable.Repeat(Question(Mc), returned).ToArray()), Spec(count: 1));
        Assert.Equal(AiErrorCodes.SchemaViolation, error.Code);
        Assert.Contains("questions", error.Message);
    }

    [Theory]
    [InlineData("{\"stem\":\"Câu\",\"payload\":{0},\"type\":\"MultipleChoice\"}")]
    [InlineData("{\"stem\":\"Câu\",\"stem\":\"Khác\",\"payload\":{0}}")]
    [InlineData("{\"payload\":{0}}")]
    [InlineData("{\"stem\":\"Câu\"}")]
    [InlineData("\"not an object\"")]
    public void Question_object_must_have_exact_keys(string template)
    {
        var question = template.Replace("{0}", Mc);
        Assert.Equal(AiErrorCodes.SchemaViolation, Invalid(Output(question), Spec()).Code);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("42")]
    [InlineData("null")]
    public void Stem_must_be_non_blank_text(string stem) =>
        Assert.Equal(AiErrorCodes.SchemaViolation, Invalid(Output(Question(Mc, stem: stem)), Spec()).Code);

    [Fact]
    public void Stem_longer_than_limit_is_schema_violation()
    {
        var stem = JsonSerializer.Serialize(new string('x', 10001));
        Assert.Equal(AiErrorCodes.SchemaViolation, Invalid(Output(Question(Mc, stem: stem)), Spec()).Code);
    }

    [Fact]
    public void Explanation_must_be_text() =>
        Assert.Equal(AiErrorCodes.SchemaViolation, Invalid(Output(Question(Mc, explanation: "1")), Spec()).Code);

    [Theory]
    [InlineData("MultipleChoice", """{"options":[{"label":"A","text":"1"},{"label":"B","text":"2"},{"label":"C","text":"3"}],"correctLabel":"B"}""")]
    [InlineData("MultipleChoice", """{"options":[{"label":"A","text":"1"},{"label":"B","text":"2"},{"label":"C","text":"3"},{"label":"D","text":"4"}],"correctLabel":"E"}""")]
    [InlineData("MultipleChoice", """{"options":[{"label":"B","text":"1"},{"label":"A","text":"2"},{"label":"C","text":"3"},{"label":"D","text":"4"}],"correctLabel":"A"}""")]
    [InlineData("MultipleChoice", """{"options":[{"label":"A","text":"1"},{"label":"B","text":"2"},{"label":"C","text":"3"},{"label":"D","text":"4"}],"correctLabel":["A","B"]}""")]
    [InlineData("TrueFalse", """{"statements":[{"label":"a","text":"x","isTrue":true},{"label":"b","text":"y","isTrue":false},{"label":"c","text":"z","isTrue":true}]}""")]
    [InlineData("TrueFalse", """{"statements":[{"label":"a","text":"x","isTrue":"true"},{"label":"b","text":"y","isTrue":false},{"label":"c","text":"z","isTrue":true},{"label":"d","text":"t","isTrue":false}]}""")]
    [InlineData("ShortAnswer", """{"answerType":"number","acceptedAnswers":[]}""")]
    [InlineData("ShortAnswer", """{"answerType":"text","acceptedAnswers":["abc"],"tolerance":0.1}""")]
    [InlineData("ShortAnswer", """{"answerType":"number","acceptedAnswers":["1e3"]}""")]
    [InlineData("Essay", """{"modelAnswer":"x","rubric":[]}""")]
    [InlineData("Essay", """{"modelAnswer":"x","rubric":[{"criterion":"a","points":0}]}""")]
    // Section 8.2 rules are shared with manual questions through QuestionPayloadRules.
    public void Payload_violating_type_rules_is_schema_violation(string type, string payload) =>
        Assert.Equal(AiErrorCodes.SchemaViolation, Invalid(Output(Question(payload)), Spec(type)).Code);

    [Fact]
    // A valid payload for another type must not be accepted for the requested type.
    public void Payload_of_another_type_is_schema_violation() =>
        Assert.Equal(AiErrorCodes.SchemaViolation, Invalid(Output(Question(Tf)), Spec("MultipleChoice")).Code);

    [Fact]
    // The message feeds the repair prompt, so it must locate the problem without echoing model text.
    public void Error_message_names_the_failing_question_without_echoing_content()
    {
        var bad = Question(Mc.Replace("\"B\"}", "\"Z-secret\"}"));
        var error = Invalid(Output(Question(Mc), bad), Spec(count: 2));

        Assert.Contains("questions[1].payload", error.Message);
        Assert.DoesNotContain("Z-secret", error.Message);
    }
}
