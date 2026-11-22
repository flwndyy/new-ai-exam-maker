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
    /// statement texts, in array order). Answers are deliberately excluded; questions of different types never collide. The payload has already passed
    /// <c>QuestionRules</c>, so its shape is trusted.
    /// </summary>
    public static string Key(string type, string stem, JsonElement payload)
    {
        var parts = new List<string> { type, AiTextNormalizer.Normalize(stem) };
        switch (type)
        {
            case "MultipleChoice":
                AddTexts(parts, payload.GetProperty("options"));
                break;
            case "TrueFalse":
                AddTexts(parts, payload.GetProperty("statements"));
                break;
        }

        return string.Join(Separator, parts);
    }

    /// <summary>Walks the questions in order; the first occurrence of a key wins, so the lowest ordinal is kept.</summary>
    public static AiDuplicateFilterResult Filter(
        string type,
        IReadOnlyList<AiGeneratedQuestion> questions,
        IReadOnlySet<string> existingKeys)
    {
        var seen = new HashSet<string>(existingKeys);
        var kept = new List<AiGeneratedQuestion>(questions.Count);
        foreach (var question in questions)
        {
            if (seen.Add(Key(type, question.Stem, question.Payload)))
                kept.Add(question);
        }

        return new AiDuplicateFilterResult(kept, questions.Count - kept.Count);
    }

    private static void AddTexts(List<string> parts, JsonElement items)
    {
        foreach (var item in items.EnumerateArray())
            parts.Add(AiTextNormalizer.Normalize(item.GetProperty("text").GetString()!));
    }
}
