using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Application.Exams;
using ExamMaker.Domain.Exams;

namespace ExamMaker.Api.Tests;

public sealed class ExamExportTests
{
    private static readonly Guid Owner = Guid.NewGuid(), Department = Guid.NewGuid();
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    internal static readonly string[] ForbiddenKeys =
        ["correctLabel", "isTrue", "acceptedAnswers", "tolerance", "modelAnswer", "rubric", "explanation", "difficulty", "sourceQuestionId", "createdBy"];

    private static AuthenticatedUser Actor(string role, bool own, bool same) =>
        new(own ? Owner : Guid.NewGuid(), "synthetic", "test@example.invalid", "Synthetic", role, same ? Department : Guid.NewGuid(), null, true);
    private static Exam Exam() => new() { Id = Guid.NewGuid(), CreatedBy = Owner, DepartmentId = Department };
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static readonly SheetHeader Header = new("Synthetic exam", "Toán", "Tổ Toán", 10, 1, 45, 3);

    public static IEnumerable<object?[]> AccessCases()
    {
        object?[][] pinned =
        [
            ["teacher", false, true, "Approved", false, "NotFound"],
            ["teacher", false, true, "Published", false, null],
            ["teacher", false, true, "Published", true, "Forbidden"],
            ["head", false, true, "Submitted", false, null],
            ["head", false, true, "Submitted", true, null],
            ["head", false, false, "Published", false, "NotFound"],
            ["teacher", true, true, "Draft", false, "ExportUnavailable"],
            ["teacher", true, true, "Rejected", true, "ExportUnavailable"],
            ["admin", false, false, "Archived", false, "ExportUnavailable"],
            ["admin", false, false, "Published", true, null],
        ];
        foreach (var row in pinned) yield return row;
        // Generated rows keep the whole role × ownership × department × state × endpoint space covered.
        foreach (var role in new[] { "teacher", "head", "admin" })
            foreach (var own in new[] { false, true })
                foreach (var same in new[] { false, true })
                    foreach (var state in ExamRules.States)
                        foreach (var answerKey in new[] { false, true })
                        {
                            if (role == "admin" && own) continue;
                            var actor = Actor(role, own, same); var exam = Exam();
                            string? code = !ExamRules.CanRead(actor, exam, state) ? "NotFound"
                                : answerKey && !ExamRules.CanReadAnswers(actor, exam) ? "Forbidden"
                                : state is "Submitted" or "Approved" or "Published" ? null : "ExportUnavailable";
                            yield return [role, own, same, state, answerKey, code];
                        }
    }

    [Theory]
    [MemberData(nameof(AccessCases))]
    public void Authorize_follows_visibility_then_answer_access_then_state(string role, bool own, bool same, string status, bool answerKey, string? code)
    {
        var actor = Actor(role, own, same); var exam = Exam();
        if (code is null) { ExamExport.Authorize(actor, exam, status, answerKey); return; }
        Assert.Equal(code, Assert.Throws<ExamException>(() => ExamExport.Authorize(actor, exam, status, answerKey)).Code);
    }

    [Theory]
    [InlineData("MultipleChoice", """{"options":[{"label":"A","text":"x","hint":"h"},{"label":"B","text":"y"}],"correctLabel":"B","hint":"h"}""", """{"options":[{"label":"A","text":"x"},{"label":"B","text":"y"}]}""")]
    [InlineData("TrueFalse", """{"statements":[{"label":"a","text":"s","isTrue":true}]}""", """{"statements":[{"label":"a","text":"s"}]}""")]
    [InlineData("ShortAnswer", """{"answerType":"number","acceptedAnswers":["12"],"tolerance":0.1,"unit":"m/s"}""", """{"answerType":"number","unit":"m/s"}""")]
    [InlineData("ShortAnswer", """{"answerType":"text","acceptedAnswers":["x"]}""", """{"answerType":"text","unit":null}""")]
    [InlineData("Essay", """{"modelAnswer":"M","rubric":[{"criterion":"c","points":1}]}""", "{}")]
    public void Student_payload_copies_only_whitelisted_fields(string type, string snapshot, string expected) =>
        Assert.Equal(expected, JsonSerializer.Serialize(ExamExport.StudentPayload(type, Parse(snapshot))));

    [Theory]
    [InlineData("Unknown", "{}")]
    [InlineData("MultipleChoice", """{"options":"bad"}""")]
    [InlineData("MultipleChoice", """{"options":[{"label":"A"}]}""")]
    [InlineData("TrueFalse", """{"statements":[{"label":"a","text":1}]}""")]
    [InlineData("ShortAnswer", """{"unit":"m"}""")]
    [InlineData("ShortAnswer", """{"answerType":"text","unit":5}""")]
    [InlineData("Essay", "[]")]
    public void Student_payload_rejects_unknown_types_and_malformed_snapshots(string type, string snapshot) =>
        Assert.Equal("Malformed exam snapshot payload.",
            Assert.Throws<InvalidOperationException>(() => ExamExport.StudentPayload(type, Parse(snapshot))).Message);

    private static ExportSnapshotItem[] Items() =>
    [
        new(2, 2, "Essay", "Application", "Essay stem", """{"modelAnswer":"MODEL-ANSWER-SENTINEL","rubric":[{"criterion":"c","points":2}]}""", "EXPLANATION-SENTINEL"),
        new(1, 1, "MultipleChoice", "Recognition", "MC stem", """{"options":[{"label":"A","text":"x"},{"label":"B","text":"y"},{"label":"C","text":"z"},{"label":"D","text":"w"}],"correctLabel":"B"}""", null),
    ];

    private static void AssertNoForbiddenKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(property.Name, ForbiddenKeys);
                AssertNoForbiddenKeys(property.Value);
            }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) AssertNoForbiddenKeys(item);
    }

    [Fact]
    public void Builders_number_by_sort_order_and_keep_answers_only_in_answer_key()
    {
        var student = ExamExport.BuildStudentSheet(Header, Items());
        Assert.Equal([1, 2], student.Items.Select(i => i.Number));
        Assert.Equal("MC stem", student.Items[0].Stem);
        var json = JsonSerializer.Serialize(student, Web);
        AssertNoForbiddenKeys(JsonDocument.Parse(json).RootElement);
        Assert.DoesNotContain("SENTINEL", json);

        var key = ExamExport.BuildAnswerKey(Header, Items());
        Assert.Equal([1, 2], key.Items.Select(i => i.Number));
        Assert.Equal("B", key.Items[0].Payload.GetProperty("correctLabel").GetString());
        Assert.Equal("EXPLANATION-SENTINEL", key.Items[1].Explanation);
        Assert.Equal("Application", key.Items[1].Difficulty);
        Assert.Contains("MODEL-ANSWER-SENTINEL", JsonSerializer.Serialize(key, Web));
    }

    [Fact]
    public void Builders_refuse_incomplete_snapshots()
    {
        ExportSnapshotItem[] items = [new(1, 1, "MultipleChoice", "Recognition", "stem", null, null)];
        Assert.Equal("ExportUnavailable", Assert.Throws<ExamException>(() => ExamExport.BuildStudentSheet(Header, items)).Code);
        Assert.Equal("ExportUnavailable", Assert.Throws<ExamException>(() => ExamExport.BuildAnswerKey(Header, items)).Code);
    }
}
