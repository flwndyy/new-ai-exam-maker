using System.Globalization;
using System.Text.Json;

namespace ExamMaker.Application.Ai.Verification;

/// <summary>
/// Compares the answer the model produced when solving a question blind with the stored payload.
/// The caller (verification parser) has already shape-checked <c>modelAnswer</c> for the type:
/// MultipleChoice string A-D, TrueFalse object with boolean a-d, ShortAnswer string.
/// </summary>
public static class AiAnswerComparer
{
    private static readonly string[] TrueFalseLabels = ["a", "b", "c", "d"];

    public static AiVerificationOutcome Compare(string type, JsonElement payload, JsonElement modelAnswer) => type switch
    {
        "MultipleChoice" => CompareMultipleChoice(payload, modelAnswer),
        "TrueFalse" => CompareTrueFalse(payload, modelAnswer),
        "ShortAnswer" => CompareShortAnswer(payload, modelAnswer),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported question type for verification."),
    };

    private static AiVerificationOutcome CompareMultipleChoice(JsonElement payload, JsonElement modelAnswer)
    {
        var correct = payload.GetProperty("correctLabel").GetString()!;
        var model = modelAnswer.GetString()!;
        return model == correct
            ? Agree()
            : Disagree(AiVerificationNotes.MultipleChoice(model, correct));
    }

    private static AiVerificationOutcome CompareTrueFalse(JsonElement payload, JsonElement modelAnswer)
    {
        var expected = new Dictionary<string, bool>();
        foreach (var statement in payload.GetProperty("statements").EnumerateArray())
        {
            expected[statement.GetProperty("label").GetString()!] = statement.GetProperty("isTrue").GetBoolean();
        }

        var mismatched = TrueFalseLabels
            .Where(label => modelAnswer.GetProperty(label).GetBoolean() != expected[label])
            .ToList();
        return mismatched.Count == 0
            ? Agree()
            : Disagree(AiVerificationNotes.TrueFalse(mismatched));
    }

    private static AiVerificationOutcome CompareShortAnswer(JsonElement payload, JsonElement modelAnswer)
    {
        var model = modelAnswer.GetString()!;
        var accepted = payload.GetProperty("acceptedAnswers").EnumerateArray().Select(a => a.GetString()!).ToList();
        var isNumber = payload.GetProperty("answerType").GetString() == "number";

        var matches = isNumber
            ? MatchesNumber(model, accepted, ReadTolerance(payload))
            : MatchesText(model, accepted);
        return matches
            ? Agree()
            : Disagree(AiVerificationNotes.ShortAnswer(model));
    }

    private static bool MatchesText(string model, IEnumerable<string> accepted)
    {
        var normalized = AiTextNormalizer.Normalize(model);
        return accepted.Any(answer => AiTextNormalizer.Normalize(answer) == normalized);
    }

    private static bool MatchesNumber(string model, IEnumerable<string> accepted, decimal? tolerance)
    {
        // Exact text match first: accepted answers with more digits than decimal can hold would never parse.
        var modelText = NormalizeNumberText(model);
        var acceptedList = accepted.ToList();
        if (acceptedList.Any(answer => NormalizeNumberText(answer) == modelText))
        {
            return true;
        }

        if (!TryParseNumber(model, out var modelValue))
        {
            return false;
        }

        foreach (var answer in acceptedList)
        {
            if (!TryParseNumber(answer, out var acceptedValue))
            {
                continue;
            }

            if (tolerance is null)
            {
                return true;
            }

            try
            {
                if (Math.Abs(modelValue - acceptedValue) <= tolerance.Value)
                {
                    return true;
                }
            }
            catch (OverflowException)
            {
                // Difference exceeds decimal range, so it is larger than any representable tolerance.
            }
        }

        return false;
    }

    private static string NormalizeNumberText(string text) => text.Trim().Replace(',', '.');

    private static bool TryParseNumber(string text, out decimal value) =>
        decimal.TryParse(
            NormalizeNumberText(text),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// Returns the tolerance, 0 when absent, or null when it is too large for decimal (every number then matches).
    /// Stored tolerance exponents reach +-1000, so decimal parsing alone could throw or lose meaning.
    /// </summary>
    private static decimal? ReadTolerance(JsonElement payload)
    {
        if (!payload.TryGetProperty("tolerance", out var tolerance) || tolerance.ValueKind != JsonValueKind.Number)
        {
            return 0m;
        }

        if (tolerance.TryGetDecimal(out var exact))
        {
            return exact;
        }

        // Not representable as decimal: huge magnitude matches everything, tiny magnitude counts as 0.
        return Math.Abs(tolerance.GetDouble()) >= 1 ? null : 0m;
    }

    private static AiVerificationOutcome Agree() => new(AiVerificationValues.Agree, null);

    private static AiVerificationOutcome Disagree(string note) => new(AiVerificationValues.Disagree, note);
}
