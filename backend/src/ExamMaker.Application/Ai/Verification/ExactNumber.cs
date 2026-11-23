using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace ExamMaker.Application.Ai.Verification;

/// <summary>
/// Exact decimal value <c>Mantissa * 10^Exponent</c>. Short-answer numbers and tolerances may exceed the range or
/// precision of <see cref="decimal"/> (accepted answers are unbounded digit strings; tolerance exponents reach +-1000),
/// so comparisons are done with arbitrary-precision integers instead.
/// </summary>
internal readonly record struct ExactNumber(BigInteger Mantissa, int Exponent)
{
    public static readonly ExactNumber Zero = new(BigInteger.Zero, 0);

    // Short-answer number text (A6 format, plus an optional leading '+' from the model); comma or dot as separator.
    private static readonly Regex Plain = new(@"^([+-]?)([0-9]+)(?:[.,]([0-9]+))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // JSON number grammar (RFC 8259).
    private static readonly Regex JsonNumber = new(@"^(-?)([0-9]+)(?:\.([0-9]+))?(?:[eE]([+-]?[0-9]+))?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Parses short-answer text such as <c>-12,5</c> or <c>12.50</c>; no exponent, unit or spaces inside.</summary>
    public static bool TryParsePlain(string text, out ExactNumber value)
    {
        var match = Plain.Match(text.Trim());
        value = match.Success ? Build(match, exponentShift: 0) : Zero;
        return match.Success;
    }

    /// <summary>Parses the raw text of a JSON number, including exponent notation.</summary>
    public static bool TryParseJson(string raw, out ExactNumber value)
    {
        value = Zero;
        var match = JsonNumber.Match(raw);
        if (!match.Success) return false;

        var shift = 0;
        if (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out shift))
            return false;

        value = Build(match, shift);
        return true;
    }

    /// <summary>True when <c>|a - b| &lt;= tolerance</c>, computed exactly.</summary>
    public static bool WithinTolerance(ExactNumber a, ExactNumber b, ExactNumber tolerance)
    {
        var exponent = Math.Min(a.Exponent, Math.Min(b.Exponent, tolerance.Exponent));
        var difference = BigInteger.Abs(a.ScaleTo(exponent) - b.ScaleTo(exponent));
        return difference <= tolerance.ScaleTo(exponent);
    }

    private BigInteger ScaleTo(int exponent) => Mantissa * BigInteger.Pow(10, Exponent - exponent);

    private static ExactNumber Build(Match match, int exponentShift)
    {
        var fraction = match.Groups[3].Success ? match.Groups[3].Value : "";
        var mantissa = BigInteger.Parse(match.Groups[2].Value + fraction, CultureInfo.InvariantCulture);
        if (match.Groups[1].Value == "-") mantissa = -mantissa;
        return new ExactNumber(mantissa, exponentShift - fraction.Length);
    }
}
