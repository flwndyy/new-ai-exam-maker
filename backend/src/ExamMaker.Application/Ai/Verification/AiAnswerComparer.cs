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

    /// <summary>
    /// Exact comparison (no <see cref="decimal"/> rounding or range limits): the model answer matches when it is within
    /// the tolerance of any accepted answer. Text that is not a plain number (unit, exponent, spaces) never matches.
    /// </summary>
    private static bool MatchesNumber(string model, IEnumerable<string> accepted, ExactNumber tolerance)
    {
        if (!ExactNumber.TryParsePlain(model, out var modelValue))
        {
            return false;
        }

        return accepted.Any(answer =>
            ExactNumber.TryParsePlain(answer, out var acceptedValue)
            && ExactNumber.WithinTolerance(modelValue, acceptedValue, tolerance));
    }

    /// <summary>The stored tolerance, or 0 (exact match) when it is absent or null.</summary>
    private static ExactNumber ReadTolerance(JsonElement payload) =>
        payload.TryGetProperty("tolerance", out var tolerance)
        && tolerance.ValueKind == JsonValueKind.Number
        && ExactNumber.TryParseJson(tolerance.GetRawText(), out var value)
            ? value
            : ExactNumber.Zero;

    private static AiVerificationOutcome Agree() => new(AiVerificationValues.Agree, null);

    private static AiVerificationOutcome Disagree(string note) => new(AiVerificationValues.Disagree, note);
}
