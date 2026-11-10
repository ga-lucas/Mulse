using System.Text.RegularExpressions;
using Mulse.CompatibilityPack.Mapping;

namespace Service.Mapping;

internal sealed record BizTalkScriptSignature(string ReturnType, string Name, IReadOnlyList<(string Type, string Name)>? Parameters, int PublicMethods)
{
    public string Display => $"{ReturnType} {Name}({(Parameters is null ? "..." : string.Join(", ", Parameters.Select(parameter => $"{parameter.Type} {parameter.Name}")))})";
}

/// <summary>Reads an inline C# scripting functoid only to describe it. The code is never compiled or run; the description seeds a stub that someone (or an AI assistant) replaces with a native formula.</summary>
internal static class BizTalkScriptStub
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Comments = new(@"/\*.*?\*/|//[^\r\n]*", RegexOptions.Singleline | RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Method = new(@"\bpublic\s+(?:static\s+)?([\w.<>\[\]?]+)\s+(\w+)\s*\(([^)]*)\)", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Formats = new(@"""([yMdHhmsfFzt][yMdHhmsfFzt\-:/. T']{3,})""", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Cases = new(@"\bcase\s+(""[^""]{0,40}""|'[^']{0,8}'|\d{1,9})\s*:", RegexOptions.CultureInvariant, Timeout);

    private static readonly (string Token, string Hint)[] Markers =
    [
        ("DateTime.Now", "reads the current time (native: DateNow)"),
        ("DateTime.UtcNow", "reads the current UTC time (native: DateNow uses the host's local time)"),
        ("Guid.NewGuid", "creates a GUID (native: NewGuid)"),
        ("TryParseExact", "parses dates with explicit formats (native: DateReformat / DateValid)"),
        ("ParseExact", "parses dates with explicit formats (native: DateReformat / DateValid)"),
        ("DateTime.Parse", "parses dates (native: DateReformat with no input formats)"),
        ("DateTime.TryParse", "parses dates (native: DateReformat with no input formats)"),
        ("ToUpper", "upper-cases text (native: StringUpperCase)"),
        ("ToLower", "lower-cases text (native: StringLowerCase)"),
        ("Trim(", "trims text (native: StringTrim)"),
        ("Replace(", "replaces text (native: StringReplace)"),
        ("Substring(", "takes part of a string (native: StringSubstring)"),
        ("Contains(", "tests for a substring (native: StringContains)"),
        ("Regex", "uses regular expressions (no native equivalent; compose from string functions or keep the stub)"),
        ("switch", "uses a switch (native: nested Conditional with LogicalEq)"),
        ("Convert.", "converts types (check numeric/date handling by hand)"),
        ("CultureInfo", "depends on a culture (Mulse functions use en-US/invariant parsing)"),
        ("File.", "touches files (cannot be a formula)"),
        ("System.IO", "touches files (cannot be a formula)"),
        ("SqlConnection", "queries a database (cannot be a formula)"),
        ("HttpClient", "calls a web service (cannot be a formula)"),
        ("WebRequest", "calls a web service (cannot be a formula)"),
        ("ConfigurationManager", "reads application configuration (cannot be a formula)"),
        ("Environment.", "reads the environment (cannot be a formula)"),
        ("static ", "keeps static state between calls (a formula has no state)")
    ];

    public static string Strip(string code)
    {
        try { return Comments.Replace(code, string.Empty); }
        catch (RegexMatchTimeoutException) { return code; }
    }

    public static BizTalkScriptSignature? Parse(string strippedCode)
    {
        MatchCollection matches;
        try { matches = Method.Matches(strippedCode); }
        catch (RegexMatchTimeoutException) { return null; }
        if (matches.Count == 0) return null;
        var first = matches[0];
        var parameters = new List<(string, string)>();
        var text = first.Groups[3].Value.Trim();
        var parsed = true;
        if (text.Length > 0)
            foreach (var part in text.Split(','))
            {
                var tokens = part.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2 || !XmlMappingFunctionCatalog.IsIdentifier(tokens[^1])) { parsed = false; break; }
                parameters.Add((string.Join(' ', tokens[..^1]), tokens[^1]));
            }
        return new(first.Groups[1].Value, first.Groups[2].Value, parsed ? parameters : null, matches.Count);
    }

    public static IReadOnlyList<string> Hints(string strippedCode)
    {
        var hints = new List<string>();
        foreach (var (token, hint) in Markers)
            if (strippedCode.Contains(token, StringComparison.Ordinal) && !hints.Contains(hint)) hints.Add(hint);
        try
        {
            var formats = Formats.Matches(strippedCode).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(8).ToArray();
            if (formats.Length > 0) hints.Add($"date/time format strings: {string.Join(", ", formats.Select(format => $"\"{format}\""))}");
            var cases = Cases.Matches(strippedCode).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(12).ToArray();
            if (cases.Length > 0) hints.Add($"case labels: {string.Join(", ", cases)}");
        }
        catch (RegexMatchTimeoutException) { }
        return hints;
    }

    public static string Clip(string text, int max)
        => text.Length <= max ? text : text[..Math.Max(0, max - 15)] + " …[truncated]";
}
