using System.Text;

namespace ExamMaker.Application.Ai.Verification;

/// <summary>Values of <c>questions.ai_verification</c>; must match the DB CHECK constraint.</summary>
public static class AiVerificationValues
{
    public const string NotChecked = "NotChecked";
    public const string Agree = "Agree";
    public const string Disagree = "Disagree";
}

/// <summary>Verification result for one question; <paramref name="Note"/> is null for <c>Agree</c> and unchecked questions.</summary>
public sealed record AiVerificationOutcome(string Verification, string? Note);

/// <summary>Fixed Vietnamese notes stored with the verification flag.</summary>
public static class AiVerificationNotes
{
    private const int MaxModelValueLength = 100;

    public const string Unavailable = "Không kiểm chứng được tự động.";
    public const string Unanswered = "Mô hình không trả lời câu này.";

    public static string MultipleChoice(string model, string correct) =>
        $"Mô hình tự giải chọn {model}, đáp án gốc là {correct}.";

    public static string TrueFalse(IEnumerable<string> labels) =>
        $"Mô hình tự giải khác đáp án gốc ở ý: {string.Join(", ", labels)}.";

    public static string ShortAnswer(string model)
    {
        var value = Sanitize(model);
        if (value.Length > MaxModelValueLength)
        {
            // Do not cut a surrogate pair in half.
            var length = char.IsHighSurrogate(value[MaxModelValueLength - 1]) ? MaxModelValueLength - 1 : MaxModelValueLength;
            value = value[..length];
        }

        return $"Mô hình tự giải ra \"{value}\", không khớp đáp án chấp nhận.";
    }

    /// <summary>
    /// The model value comes from arbitrary JSON text. PostgreSQL <c>text</c> rejects NUL and the note must be valid
    /// UTF-16, so control characters become spaces and unpaired surrogates are dropped.
    /// </summary>
    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsControl(c))
            {
                builder.Append(' ');
            }
            else if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[++i]);
            }
            else if (!char.IsSurrogate(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
