using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenSage.FileFormats;

public static class ParseUtility
{
    private static readonly Regex FloatRegex = new Regex("^\\s*([-+]?[0-9]*\\.?[0-9]+)", RegexOptions.Compiled);

    // Retail's INI/asset field scanners are sscanf-based: scanInt is sscanf(token, "%d", &value),
    // which skips leading whitespace, reads an optional sign and the leading run of digits, and
    // then simply STOPS at the first non-digit character. A float-shaped token such as "2." or
    // "2.75" therefore yields the integer 2 rather than an error, and only a token with no leading
    // digit run at all (e.g. "" or "abc") is a data error. This regex reproduces that scan.
    private static readonly Regex IntegerRegex = new Regex("^\\s*([-+]?[0-9]+)", RegexOptions.Compiled);

    public static float ParseFloat(string s)
    {
        s = ExtractFloat(s);
        return float.Parse(s, CultureInfo.InvariantCulture);
    }

    public static bool TryParseFloat(string s, out float result)
    {
        s = ExtractFloat(s);
        return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
    }

    private static string ExtractFloat(string s)
    {
        var match = FloatRegex.Match(s);
        return match.Success
            ? match.Groups[1].Value
            : string.Empty;
    }

    public static string ToInvariant(float number)
    {
        return number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Parses the leading integer of <paramref name="s"/> with sscanf("%d") semantics: leading
    /// whitespace is skipped, an optional sign and the leading digit run are consumed, and any
    /// trailing characters are ignored. Float-shaped tokens ("2.", "2.75") truncate towards zero
    /// on their integer part. Throws <see cref="FormatException"/> when there is no digit run.
    /// </summary>
    public static int ParseInteger(string s)
    {
        var extracted = ExtractInteger(s);
        if (extracted.Length == 0)
        {
            throw new FormatException($"The input string '{s}' does not contain an integer.");
        }

        // Convert.ToInt32 (not int.TryParse) so that a digit run too large for Int32 still raises
        // OverflowException, which callers such as IniParser.ScanLong clamp on.
        return Convert.ToInt32(extracted, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Non-throwing counterpart of <see cref="ParseInteger"/>. Returns false (and 0) when
    /// <paramref name="s"/> has no leading digit run.
    /// </summary>
    public static bool TryParseInteger(string s, out int result)
    {
        result = 0;
        var extracted = ExtractInteger(s);
        return extracted.Length != 0
            && int.TryParse(extracted, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// True when <paramref name="s"/> would parse as an integer under <see cref="ParseInteger"/>.
    /// </summary>
    public static bool IsInteger(string s) => TryParseInteger(s, out _);

    private static string ExtractInteger(string s)
    {
        if (s is null)
        {
            return string.Empty;
        }

        var match = IntegerRegex.Match(s);
        return match.Success
            ? match.Groups[1].Value
            : string.Empty;
    }

    public static long ParseLong(string s)
    {
        var extracted = ExtractInteger(s);
        if (extracted.Length == 0)
        {
            throw new FormatException($"The input string '{s}' does not contain an integer.");
        }

        // Convert.ToInt64 preserves the OverflowException that IniParser.ScanLong clamps on.
        return Convert.ToInt64(extracted, CultureInfo.InvariantCulture);
    }

    public static bool TryParseLong(string s, out long result)
    {
        result = 0;
        var extracted = ExtractInteger(s);
        return extracted.Length != 0
            && long.TryParse(extracted, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }

    /// <summary>
    /// Parses the leading unsigned integer of <paramref name="s"/> with sscanf("%u") semantics.
    /// As with "%d", a float-shaped token truncates at the decimal point, and a negative digit run
    /// wraps rather than failing. Throws <see cref="FormatException"/> when there is no digit run.
    /// </summary>
    public static uint ParseUnsignedInteger(string s)
    {
        if (!TryParseUnsignedInteger(s, out var result))
        {
            throw new FormatException($"The input string '{s}' does not contain an integer.");
        }

        return result;
    }

    public static bool TryParseUnsignedInteger(string s, out uint result)
    {
        result = 0;

        var extracted = ExtractInteger(s);
        if (extracted.Length == 0)
        {
            return false;
        }

        if (extracted[0] == '-')
        {
            // sscanf("%u") wraps a negative value into the unsigned range rather than rejecting it.
            if (!long.TryParse(extracted, NumberStyles.Integer, CultureInfo.InvariantCulture, out var signed))
            {
                return false;
            }

            result = unchecked((uint)signed);
            return true;
        }

        return uint.TryParse(extracted, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
    }
}
