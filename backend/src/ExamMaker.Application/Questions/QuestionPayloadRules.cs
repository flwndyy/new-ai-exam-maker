using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExamMaker.Application.Questions;

public static class QuestionPayloadRules
{
    public const int MaxTextLength = 10000;
    public const int MaxAnswers = 20;
    public const int MaxRubricItems = 20;
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
                Array(answers, 1, MaxAnswers);
                foreach (var answer in answers.EnumerateArray())
                {
                    var value = Text(answer);
                    // Accept Vietnamese decimal commas without treating punctuation as a thousands separator or rewriting stored answers.
                    if (answerType == "number" && (value != value.Trim()
                        || !Regex.IsMatch(value, @"^-?[0-9]+([.,][0-9]+)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)
                        || !double.TryParse(value.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                            CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))) Fail();
                }
                if (payload.TryGetProperty("unit", out var unit) && unit.ValueKind != JsonValueKind.Null) Text(unit, true);
                if (payload.TryGetProperty("tolerance", out var tolerance) && tolerance.ValueKind != JsonValueKind.Null)
                {
                    if (answerType != "number" || !IsStorageSafeNumber(tolerance, allowZero: true)) Fail();
                }
                break;
            case "Essay":
                Object(payload, ["modelAnswer", "rubric"]);
                Text(payload.GetProperty("modelAnswer"), true);
                var rubric = payload.GetProperty("rubric");
                Array(rubric, 1, MaxRubricItems);
                foreach (var item in rubric.EnumerateArray())
                {
                    Object(item, ["criterion", "points"]);
                    Text(item.GetProperty("criterion"));
                    var points = item.GetProperty("points");
                    if (!IsStorageSafeNumber(points, allowZero: false)) Fail();
                }
                break;
            default: Fail(); break;
        }
    }

    /// <summary>Bound PostgreSQL's expanded decimal representation so creation and post-jsonb revalidation accept the same numbers.</summary>
    private static bool IsStorageSafeNumber(JsonElement value, bool allowZero)
    {
        if (value.ValueKind != JsonValueKind.Number) return false;
        var raw = value.GetRawText();
        var exponentIndex = raw.IndexOfAny(['e', 'E']);
        var exponent = 0;
        if (exponentIndex >= 0 && (!int.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent) || exponent is < -1000 or > 1000)) return false;
        var negative = raw[0] == '-';
        var mantissa = raw.AsSpan(negative ? 1 : 0, (exponentIndex < 0 ? raw.Length : exponentIndex) - (negative ? 1 : 0));
        var dot = mantissa.IndexOf('.');
        var integerDigits = dot < 0 ? mantissa.Length : dot;
        var fractionalDigits = dot < 0 ? 0 : mantissa.Length - dot - 1;
        var firstNonzero = -1;
        var position = 0;
        foreach (var digit in mantissa)
        {
            if (digit == '.') continue;
            if (firstNonzero < 0 && digit is >= '1' and <= '9') firstNonzero = position;
            position++;
        }
        var nonzero = firstNonzero >= 0;
        if (negative && nonzero || !allowZero && !nonzero) return false;
        var expandedIntegerDigits = nonzero ? Math.Max(1L, (long)integerDigits + exponent - firstNonzero) : 1L;
        var expandedFractionalDigits = Math.Max(0L, (long)fractionalDigits - exponent);
        var expandedLength = expandedIntegerDigits + (expandedFractionalDigits > 0 ? expandedFractionalDigits + 1 : 0);
        return expandedLength <= 1024;
    }

    /// <summary>Reject extra, missing and duplicate JSON properties instead of silently choosing one value.</summary>
    private static void Object(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) Fail();
        string[] keys;
        try { keys = value.EnumerateObject().Select(x => x.Name).ToArray(); }
        catch (InvalidOperationException) { Fail(); return; }
        if (keys.Distinct().Count() != keys.Length || required.Any(x => !keys.Contains(x))
            || keys.Any(x => !required.Contains(x) && !(optional?.Contains(x) ?? false))) Fail();
    }

    /// <summary>Require canonical A-D/a-d display order and explicit boolean answers for true/false items.</summary>
    private static void Labels(JsonElement value, string[] labels, bool truth)
    {
        Array(value, 4, 4);
        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            Object(item, truth ? ["label", "text", "isTrue"] : ["label", "text"]);
            var label = Text(item.GetProperty("label"));
            if (label != labels[index++]) Fail();
            Text(item.GetProperty("text"));
            if (truth && item.GetProperty("isTrue").ValueKind is not (JsonValueKind.True or JsonValueKind.False)) Fail();
        }
    }

    /// <summary>Validate a JSON string without interpreting or rendering its untrusted Markdown content.</summary>
    private static string Text(JsonElement value, bool allowEmpty = false)
    {
        if (value.ValueKind != JsonValueKind.String) Fail();
        string text;
        try { text = value.GetString()!; }
        catch (InvalidOperationException) { Fail(); return string.Empty; }
        if ((!allowEmpty && string.IsNullOrWhiteSpace(text)) || text.Length > MaxTextLength || text.Contains('\0')) Fail();
        return text;
    }

    /// <summary>Reject scalar values and arrays outside the schema's bounded item count.</summary>
    private static void Array(JsonElement value, int minimum, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || (value.GetArrayLength() < minimum || value.GetArrayLength() > maximum)) Fail();
    }

    private static void Fail() => throw QuestionRules.Invalid("payload");
}
