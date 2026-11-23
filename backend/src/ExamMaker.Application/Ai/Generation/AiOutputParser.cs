using System.Text.Json;
using System.Text.RegularExpressions;
using ExamMaker.Application.Questions;

namespace ExamMaker.Application.Ai.Generation;

/// <summary>
/// Parse and validate model output against the generation schema (docs/context.md 8.2).
/// Output is untrusted: every question goes through the same QuestionRules as manual input.
/// Exception messages locate the problem for the repair prompt and never echo model text.
/// </summary>
public static class AiOutputParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private static readonly Regex Fence = new(@"^```[A-Za-z]*[ \t]*\r?\n(?<body>.*?)\r?\n?```$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static IReadOnlyList<AiGeneratedQuestion> Parse(string text, AiGenerationSpec spec)
    {
        using var document = ParseJson(StripFence(text));
        var root = document.RootElement;
        if (!HasExactKeys(root, ["questions"], []))
            throw Schema("root: expected an object with exactly one property \"questions\".");

        var items = root.GetProperty("questions");
        if (items.ValueKind != JsonValueKind.Array)
            throw Schema("questions: expected an array.");
        if (items.GetArrayLength() != spec.Count)
            throw Schema($"questions: expected exactly {spec.Count} items but got {items.GetArrayLength()}.");

        var questions = new List<AiGeneratedQuestion>(spec.Count);
        var index = 0;
        foreach (var item in items.EnumerateArray())
        {
            questions.Add(ParseQuestion(item, index, spec));
            index++;
        }

        return questions;
    }

    private static AiGeneratedQuestion ParseQuestion(JsonElement item, int index, AiGenerationSpec spec)
    {
        var path = $"questions[{index}]";
        if (!HasExactKeys(item, ["stem", "payload"], ["explanation"]))
            throw Schema($"{path}: expected an object with properties \"stem\", \"payload\" and optional \"explanation\".");

        var stem = ReadString(item.GetProperty("stem"), $"{path}.stem", allowNull: false)!;
        var explanation = item.TryGetProperty("explanation", out var value)
            ? ReadString(value, $"{path}.explanation", allowNull: true)
            : null;

        QuestionInput input;
        try
        {
            input = QuestionRules.Validate(new QuestionInput(spec.SubjectId, spec.TopicId, spec.Grade,
                spec.Type, spec.Difficulty, stem, item.GetProperty("payload"), explanation));
        }
        catch (QuestionException exception)
        {
            var field = exception.Field ?? "payload";
            var hint = field == "payload" ? $" (does not satisfy the {spec.Type} payload rules)" : "";
            throw Schema($"{path}.{field}: invalid{hint}.");
        }

        return new AiGeneratedQuestion(index + 1, input.Stem, input.Payload.Clone(), input.Explanation);
    }

    internal static string StripFence(string text)
    {
        var trimmed = text.Trim();
        var match = Fence.Match(trimmed);
        return match.Success ? match.Groups["body"].Value : trimmed;
    }

    internal static JsonDocument ParseJson(string text)
    {
        try
        {
            return JsonDocument.Parse(text, Options);
        }
        catch (JsonException)
        {
            throw new AiOutputException(AiErrorCodes.InvalidJson, "Output is not a single valid JSON document.");
        }
    }

    private static string? ReadString(JsonElement value, string path, bool allowNull)
    {
        if (allowNull && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw Schema($"{path}: expected a string.");
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            throw Schema($"{path}: invalid string.");
        }
    }

    /// <summary>Reject missing, unknown and duplicate properties instead of silently picking one value.</summary>
    internal static bool HasExactKeys(JsonElement value, string[] required, string[] optional)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        string[] keys;
        try
        {
            keys = value.EnumerateObject().Select(x => x.Name).ToArray();
        }
        catch (InvalidOperationException)
        {
            // A property name with an unpaired surrogate escape cannot be decoded; treat it as an unknown property.
            return false;
        }

        return keys.Distinct(StringComparer.Ordinal).Count() == keys.Length
            && required.All(keys.Contains)
            && keys.All(x => required.Contains(x) || optional.Contains(x));
    }

    private static AiOutputException Schema(string message) => new(AiErrorCodes.SchemaViolation, message);
}
