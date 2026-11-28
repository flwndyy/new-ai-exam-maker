using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ExamMaker.Application.Ai.Generation;

/// <param name="Kept">Questions that are not duplicates, with their original ordinals (gaps are allowed).</param>
/// <param name="Dropped">Number of duplicate questions removed.</param>
public sealed record AiDuplicateFilterResult(IReadOnlyList<AiGeneratedQuestion> Kept, int Dropped);

/// <summary>
/// Drops generated questions that repeat an existing bank question or an earlier question of the same batch.
/// Pure logic: the caller supplies the keys of the bank questions of the same subject and topic.
/// </summary>
public static class AiDuplicateFilter
{
    private const char Separator = '\u001F';

    /// <summary>
    /// Duplicate key = question type + normalized stem + normalized text of the question part of the payload (option texts or
    /// statement texts, in array order). Answers are deliberately excluded; questions of different types never collide.
    /// For payloads that passed <c>QuestionRules</c>; throws <see cref="ArgumentException"/> on a malformed payload.
    /// </summary>
    public static string Key(string type, string stem, JsonElement payload) =>
        TryKey(type, stem, payload, out var key)
            ? key
            : throw new ArgumentException("Payload does not have the shape required for a duplicate key.", nameof(payload));

    /// <summary>
    /// Same key as <see cref="Key"/>, but returns false instead of throwing when the stem or payload shape is unusable
    /// (missing/non-array options or statements, items without a string "text"). Used for stored rows, which the DB does
    /// not shape-check.
    /// </summary>
    public static bool TryKey(string type, string stem, JsonElement payload, [NotNullWhen(true)] out string? key)
    {
        key = null;
        if (stem is null) return false;

        var parts = new List<string> { type, AiTextNormalizer.Normalize(stem) };
        var itemsProperty = type switch
        {
            "MultipleChoice" => "options",
            "TrueFalse" => "statements",
            _ => null,
        };
        if (itemsProperty is not null && !TryAddTexts(parts, payload, itemsProperty)) return false;

        key = string.Join(Separator, parts);
        return true;
    }

    /// <summary>Walks the questions in order; the first occurrence of a key wins, so the lowest ordinal is kept.</summary>
    public static AiDuplicateFilterResult Filter(
        string type,
        IReadOnlyList<AiGeneratedQuestion> questions,
        IReadOnlySet<string> existingKeys)
    {
        var seen = new HashSet<string>(existingKeys, StringComparer.Ordinal);
        var kept = new List<AiGeneratedQuestion>(questions.Count);
        foreach (var question in questions)
        {
            if (seen.Add(Key(type, question.Stem, question.Payload)))
                kept.Add(question);
        }

        return new AiDuplicateFilterResult(kept, questions.Count - kept.Count);
    }

    private static bool TryAddTexts(List<string> parts, JsonElement payload, string itemsProperty)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty(itemsProperty, out var items)
            || items.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object
                || !item.TryGetProperty("text", out var text)
                || text.ValueKind != JsonValueKind.String)
                return false;

            try
            {
                parts.Add(AiTextNormalizer.Normalize(text.GetString()!));
            }
            catch (InvalidOperationException)
            {
                // Unpaired surrogate escape: the string cannot be decoded.
                return false;
            }
        }

        return true;
    }
}
