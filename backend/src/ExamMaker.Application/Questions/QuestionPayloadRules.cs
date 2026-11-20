using System.Globalization;
using System.Text.Json;

namespace ExamMaker.Application.Questions;

public static class QuestionPayloadRules
{
    /// <summary>Validate the exact type-specific OpenAPI payload, including labels, answers and rubric points.</summary>
    public static void Validate(string type, JsonElement payload)
    {
        switch (type)
        {
            case "MultipleChoice":
                Object(payload, ["options", "correctLabel"]);
                Labels(payload.GetProperty("options"), ["A", "B", "C", "D"], false);
                if (!new[] { "A", "B", "C", "D" }.Contains(Text(payload.GetProperty("correctLabel")))) Fail();
                break;
            case "TrueFalse":
                Object(payload, ["statements"]);
                Labels(payload.GetProperty("statements"), ["a", "b", "c", "d"], true);
                break;
            case "ShortAnswer":
                Object(payload, ["answerType", "acceptedAnswers"], ["unit", "tolerance"]);
                var answerType = Text(payload.GetProperty("answerType"));
                if (answerType is not ("number" or "text")) Fail();
                var answers = payload.GetProperty("acceptedAnswers");
                Array(answers, 1);
                foreach (var answer in answers.EnumerateArray())
                {
                    var value = Text(answer);
                    if (answerType == "number" && (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                        || !double.IsFinite(number))) Fail();
                }
                if (payload.TryGetProperty("unit", out var unit) && unit.ValueKind != JsonValueKind.Null) Text(unit, true);
                if (payload.TryGetProperty("tolerance", out var tolerance) && tolerance.ValueKind != JsonValueKind.Null)
                {
                    if (answerType != "number" || tolerance.ValueKind != JsonValueKind.Number || !tolerance.TryGetDecimal(out var number) || number < 0) Fail();
                }
                break;
            case "Essay":
                Object(payload, ["modelAnswer", "rubric"]);
                Text(payload.GetProperty("modelAnswer"), true);
                var rubric = payload.GetProperty("rubric");
                Array(rubric, 1);
                foreach (var item in rubric.EnumerateArray())
                {
                    Object(item, ["criterion", "points"]);
                    Text(item.GetProperty("criterion"));
                    var points = item.GetProperty("points");
                    if (points.ValueKind != JsonValueKind.Number || !points.TryGetDecimal(out var value) || value <= 0) Fail();
                }
                break;
            default: Fail(); break;
        }
    }

    /// <summary>Reject extra, missing and duplicate JSON properties instead of silently choosing one value.</summary>
    private static void Object(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail();
        var keys = value.EnumerateObject().Select(x => x.Name).ToArray();
        if (keys.Distinct().Count() != keys.Length || required.Any(x => !keys.Contains(x))
            || keys.Any(x => !required.Contains(x) && !(optional?.Contains(x) ?? false))) Fail();
    }

    /// <summary>Require four distinct labelled options/statements and explicit boolean answers for true/false items.</summary>
    private static void Labels(JsonElement value, string[] labels, bool truth)
    {
        Array(value, 4);
        if (value.GetArrayLength() != 4) Fail();
        var seen = new HashSet<string>();
        foreach (var item in value.EnumerateArray())
        {
            Object(item, truth ? ["label", "text", "isTrue"] : ["label", "text"]);
            var label = Text(item.GetProperty("label"));
            if (!labels.Contains(label) || !seen.Add(label)) Fail();
            Text(item.GetProperty("text"));
            if (truth && item.GetProperty("isTrue").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail();
        }
    }

    /// <summary>Validate a JSON string without interpreting or rendering its untrusted Markdown content.</summary>
    private static string Text(JsonElement value, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String) Fail();
        var text = value.GetString()!;
        if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Contains('\0')) Fail();
        return text;
    }

    /// <summary>Reject scalar values or arrays below the schema's required minimum size.</summary>
    private static void Array(JsonElement value, int minimum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() < minimum) Fail();
    }

    private static void Fail() => throw QuestionRules.Invalid("payload");
}
