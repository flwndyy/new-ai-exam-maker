using System.Text;
using System.Text.RegularExpressions;

namespace ExamMaker.Application.Ai;

/// <summary>
/// Canonical form of AI text used for duplicate keys and text answer comparison: NFC, lower-case invariant,
/// whitespace runs collapsed to one space, trimmed. Punctuation and LaTeX are kept as is.
/// </summary>
public static partial class AiTextNormalizer
{
    public static string Normalize(string text)
    {
        var composed = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return Whitespace().Replace(composed, " ").Trim();
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
