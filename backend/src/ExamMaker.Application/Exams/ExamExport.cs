using System.Text.Json;
using ExamMaker.Application.Authentication;
using ExamMaker.Domain.Exams;

namespace ExamMaker.Application.Exams;

public sealed record SheetHeader(string ExamTitle, string SubjectName, string? DepartmentName, int Grade, int VersionNo,
    int DurationMinutes, decimal TotalPoints);
public sealed record StudentSheetItem(int Number, decimal Points, string Type, string Stem, JsonElement Payload);
public sealed record StudentSheet(SheetHeader Header, IReadOnlyList<StudentSheetItem> Items);
public sealed record AnswerKeyItem(int Number, decimal Points, string Type, string Difficulty, string Stem, JsonElement Payload, string? Explanation);
public sealed record AnswerKey(SheetHeader Header, IReadOnlyList<AnswerKeyItem> Items);

/// <summary>Frozen snapshot columns of one exam item, decoupled from EF so export mapping stays pure.</summary>
public sealed record ExportSnapshotItem(int SortOrder, decimal Points, string? Type, string? Difficulty, string? Stem,
    string? PayloadJson, string? Explanation);

/// <summary>Build student sheets and answer keys from immutable snapshots without ever leaking answers to students.</summary>
public static class ExamExport
{
    public static readonly string[] ExportableStates = ["Submitted", "Approved", "Published"];
    private const string Malformed = "Malformed exam snapshot payload.";

    /// <summary>Hide invisible versions as missing, then require answer access for keys, then require a frozen exportable state.</summary>
    public static void Authorize(AuthenticatedUser actor, Exam exam, string status, bool answerKey)
    {
        if (!ExamRules.CanRead(actor, exam, status)) throw ExamRules.Missing();
        if (answerKey && !ExamRules.CanReadAnswers(actor, exam)) throw ExamRules.Forbidden();
        if (!ExportableStates.Contains(status)) throw Unavailable();
    }

    public static ExamException Unavailable() =>
        new(409, "ExportUnavailable", "Chỉ xuất được phiên bản đã gửi duyệt, đã duyệt hoặc đã công bố.");

    /// <summary>Copy only student-visible fields per question type; unknown keys, future answer fields and malformed data never pass through.</summary>
    public static JsonElement StudentPayload(string type, JsonElement snapshot)
    {
        if (snapshot.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(Malformed);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            switch (type)
            {
                case "MultipleChoice": WriteLabeled(writer, snapshot, "options"); break;
                case "TrueFalse": WriteLabeled(writer, snapshot, "statements"); break;
                case "ShortAnswer":
                    writer.WriteString("answerType", RequiredString(snapshot, "answerType"));
                    if (!snapshot.TryGetProperty("unit", out var unit) || unit.ValueKind == JsonValueKind.Null) writer.WriteNull("unit");
                    else if (unit.ValueKind == JsonValueKind.String) writer.WriteString("unit", unit.GetString());
                    else throw new InvalidOperationException(Malformed);
                    break;
                case "Essay": break;
                default: throw new InvalidOperationException(Malformed);
            }
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    /// <summary>Student sheet: ordered stems and whitelisted payloads only.</summary>
    public static StudentSheet BuildStudentSheet(SheetHeader header, IEnumerable<ExportSnapshotItem> items) =>
        new(header, Complete(items).Select(i => new StudentSheetItem(i.SortOrder, i.Points, i.Type!, i.Stem!,
            StudentPayload(i.Type!, Parse(i.PayloadJson!)))).ToArray());

    /// <summary>Answer key: the full frozen payload, difficulty and explanation for teachers.</summary>
    public static AnswerKey BuildAnswerKey(SheetHeader header, IEnumerable<ExportSnapshotItem> items) =>
        new(header, Complete(items).Select(i => new AnswerKeyItem(i.SortOrder, i.Points, i.Type!, i.Difficulty!, i.Stem!,
            Parse(i.PayloadJson!), i.Explanation)).ToArray());

    private static ExportSnapshotItem[] Complete(IEnumerable<ExportSnapshotItem> items)
    {
        var ordered = items.OrderBy(i => i.SortOrder).ToArray();
        if (ordered.Any(i => i.Type is null || i.Difficulty is null || i.Stem is null || i.PayloadJson is null)) throw Unavailable();
        return ordered;
    }

    private static JsonElement Parse(string json)
    {
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch (JsonException) { throw new InvalidOperationException(Malformed); }
    }

    private static void WriteLabeled(Utf8JsonWriter writer, JsonElement snapshot, string property)
    {
        if (!snapshot.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array) throw new InvalidOperationException(Malformed);
        writer.WriteStartArray(property);
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) throw new InvalidOperationException(Malformed);
            writer.WriteStartObject();
            writer.WriteString("label", RequiredString(entry, "label"));
            writer.WriteString("text", RequiredString(entry, "text"));
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static string RequiredString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()! : throw new InvalidOperationException(Malformed);
}
