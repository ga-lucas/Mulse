using System.Globalization;

namespace Mulse.CompatibilityPack;

/// <summary>
/// XSLT extension object behind <see cref="Mulse.CompatibilityPack.Mapping.XmlMappingFunction"/>. The bodies are ports of the helper
/// methods BizTalk compiles into a map for the matching functoid (taken from generated map code), so edge cases
/// such as non-numeric counts, thousands separators and ordinal string comparison behave the same. Method names are the
/// XSLT function names and must stay public; overloads are not allowed.
/// </summary>
public sealed class XmlMappingFunctions(TimeProvider timeProvider)
{
    public const string Namespace = "urn:mulse:xml-mapping-functions";
    private const NumberStyles Numeric = NumberStyles.AllowThousands | NumberStyles.Float;

    private static bool IsNumeric(string? value, out double number)
    {
        number = 0;
        return value is not null && double.TryParse(value, Numeric, CultureInfo.InvariantCulture, out number);
    }

    private static string Format(double value) => value.ToString("G15", CultureInfo.InvariantCulture);

    public string StringLeft(string str, string count)
        => str is not null && IsNumeric(count, out var d) && (int)d is var i && i > 0 ? i <= str.Length ? str[..i] : str : string.Empty;

    public string StringRight(string str, string count)
        => str is not null && IsNumeric(count, out var d) && (int)d is var i && i > 0 ? i <= str.Length ? str[^i..] : str : string.Empty;

    public double StringSize(string str) => str?.Length ?? 0;

    public string StringUpperCase(string str) => str is null ? string.Empty : str.ToUpper(CultureInfo.InvariantCulture);

    /// <summary>Rounds toward negative infinity; non-numeric or out-of-range input yields the empty string.</summary>
    public string MathInt(string val)
    {
        if (!IsNumeric(val, out var d)) return string.Empty;
        try
        {
            var i = System.Convert.ToInt32(d, CultureInfo.InvariantCulture);
            if (i > d) i--;
            return i.ToString(CultureInfo.InvariantCulture);
        }
        catch (OverflowException) { return string.Empty; }
    }

    /// <summary>Empty when either operand is non-numeric or the divisor is zero. Numbers use 15 significant digits like the .NET Framework BizTalk runs on.</summary>
    public string MathDivide(string val1, string val2)
        => IsNumeric(val1, out var d1) && IsNumeric(val2, out var d2) && d2 != 0 ? Format(d1 / d2) : string.Empty;

    public string DateCurrentDate() => timeProvider.GetLocalNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public string DateCurrentDateTime()
    {
        var now = timeProvider.GetLocalNow();
        return now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "T" + now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // Both numeric: numeric comparison; otherwise ordinal string comparison.
    public bool LogicalGt(string val1, string val2)
        => IsNumeric(val1, out var d1) && IsNumeric(val2, out var d2) ? d1 > d2 : string.CompareOrdinal(val1, val2) > 0;

    public bool LogicalEq(string val1, string val2)
        => IsNumeric(val1, out var d1) && IsNumeric(val2, out var d2) ? d1 == d2 : string.CompareOrdinal(val1, val2) == 0;

    public bool LogicalNe(string val1, string val2)
        => IsNumeric(val1, out var d1) && IsNumeric(val2, out var d2) ? d1 != d2 : string.CompareOrdinal(val1, val2) != 0;

    public bool LogicalAnd(string param0, string param1) => ToBool(param0) && ToBool(param1);

    public bool LogicalExistence(bool val) => val;

    // Mulse-native functions. These are not BizTalk functoids; they exist so imported script stubs can be replaced by formulas.
    private static readonly CultureInfo UsCulture = CultureInfo.GetCultureInfo("en-US");

    public string NewGuid() => Guid.NewGuid().ToString("D");

    public string DateNow(string format) => timeProvider.GetLocalNow().ToString(CheckFormat(format), CultureInfo.InvariantCulture);

    public string DateReformat(string text, string outputFormat, string inputFormats)
        => TryParseDate(text, inputFormats, out var date) ? date.ToString(CheckFormat(outputFormat), CultureInfo.InvariantCulture) : string.Empty;

    public bool DateValid(string text, string inputFormats) => TryParseDate(text, inputFormats, out _);

    public string Conditional(string condition, string whenTrue, string whenFalse) => ToBool(condition) ? whenTrue : whenFalse;

    public bool LogicalOr(string param0, string param1) => ToBool(param0) || ToBool(param1);

    public bool LogicalNot(string param0) => !ToBool(param0);

    public string StringLowerCase(string str) => str is null ? string.Empty : str.ToLower(CultureInfo.InvariantCulture);

    public string StringTrim(string str) => str?.Trim() ?? string.Empty;

    public string StringReplace(string str, string find, string replacement)
        => str is null ? string.Empty : string.IsNullOrEmpty(find) ? str : str.Replace(find, replacement ?? string.Empty, StringComparison.Ordinal);

    public string StringSubstring(string str, string start, string length)
        => str is not null && IsNumeric(start, out var s) && IsNumeric(length, out var l)
            && s >= 0 && s <= str.Length && l >= 0 ? str.Substring((int)s, (int)Math.Min(l, str.Length - (int)s)) : string.Empty;

    public bool StringContains(string str, string search) => str is not null && search is not null && str.Contains(search, StringComparison.Ordinal);

    /// <summary>Raised when a map calls a custom function that is still an imported stub.</summary>
    public string Unimplemented(string name) => throw new InvalidOperationException($"Custom function '{name}' is an unimplemented stub. Supply its formula before running this map.");

    private static string CheckFormat(string format)
        => string.IsNullOrEmpty(format) || format.Length > 64 ? throw new FormatException("A date format must be 1 to 64 characters.") : format;

    private static bool TryParseDate(string? text, string? inputFormats, out DateTime date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        return string.IsNullOrEmpty(inputFormats)
            ? DateTime.TryParse(text, UsCulture, DateTimeStyles.None, out date)
            : DateTime.TryParseExact(text, inputFormats.Split('|', StringSplitOptions.RemoveEmptyEntries), UsCulture, DateTimeStyles.None, out date);
    }

    private static bool ToBool(string? value)
    {
        if (value is null) return false;
        var trimmed = value.Trim();
        if (string.Equals(trimmed, bool.TrueString, StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(trimmed, bool.FalseString, StringComparison.OrdinalIgnoreCase)) return false;
        return IsNumeric(trimmed, out var d) && d > 0;
    }
}
