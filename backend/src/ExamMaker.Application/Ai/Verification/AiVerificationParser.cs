using System.Text.Json;
using ExamMaker.Application.Ai.Generation;

namespace ExamMaker.Application.Ai.Verification;

/// <summary>
/// Parses the blind-solve output. Strict on purpose: any deviation makes the whole batch "unavailable"
/// instead of guessing, and messages never echo model text.
/// </summary>
public static class AiVerificationParser
{
    private static readonly string[] TrueFalseLabels = ["a", "b", "c", "d"];

    /// <returns>Answers keyed by ordinal; the values are shape-checked for <paramref name="type"/>.</returns>
    /// <exception cref="AiOutputException">Invalid JSON or schema violation.</exception>
    public static IReadOnlyDictionary<int, JsonElement> Parse(string text, string type, IReadOnlyCollection<int> ordinals)
    {
        using var document = AiOutputParser.ParseJson(AiOutputParser.StripFence(text));
        var root = document.RootElement;
        if (!AiOutputParser.HasExactKeys(root, ["answers"], []))
            throw Schema("root: expected an object with exactly one property \"answers\".");

        var items = root.GetProperty("answers");
        if (items.ValueKind != JsonValueKind.Array)
            throw Schema("answers: expected an array.");

        var answers = new Dictionary<int, JsonElement>();
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            var path = $"answers[{index}]";
            if (!AiOutputParser.HasExactKeys(item, ["ordinal", "answer"], []))
                throw Schema($"{path}: expected an object with exactly \"ordinal\" and \"answer\".");

            var ordinalValue = item.GetProperty("ordinal");
            if (ordinalValue.ValueKind != JsonValueKind.Number || !ordinalValue.TryGetInt32(out var ordinal) || !ordinals.Contains(ordinal))
                throw Schema($"{path}.ordinal: expected one of the requested question ordinals.");

            var answer = item.GetProperty("answer");
            if (!IsValidAnswer(type, answer))
                throw Schema($"{path}.answer: invalid format for {type}.");

            if (!answers.TryAdd(ordinal, answer.Clone()))
                throw Schema($"{path}.ordinal: duplicate ordinal.");

            index++;
        }

        return answers;
    }

    private static bool IsValidAnswer(string type, JsonElement answer) => type switch
    {
        "MultipleChoice" => answer.ValueKind == JsonValueKind.String && TryGetString(answer) is "A" or "B" or "C" or "D",
        "TrueFalse" => IsTrueFalse(answer),
        "ShortAnswer" => answer.ValueKind == JsonValueKind.String && TryGetString(answer) is not null,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported question type for verification."),
    };

    /// <summary>Returns null for a string that cannot be decoded (for example an unpaired surrogate escape).</summary>
    private static string? TryGetString(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsTrueFalse(JsonElement answer) =>
        AiOutputParser.HasExactKeys(answer, TrueFalseLabels, [])
        && TrueFalseLabels.All(label => answer.GetProperty(label).ValueKind is JsonValueKind.True or JsonValueKind.False);

    private static AiOutputException Schema(string message) => new(AiErrorCodes.SchemaViolation, message);
}
