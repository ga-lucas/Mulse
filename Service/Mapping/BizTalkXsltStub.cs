using System.Text.RegularExpressions;

namespace Service.Mapping;

/// <summary>Reads an inline XSLT scripting functoid only to describe it. The XSLT is never compiled or run; the description seeds a stub that must be rebuilt as native fields, repeats or formulas.</summary>
internal static class BizTalkXsltStub
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);
    private static readonly Regex Sample = new(@"Uncomment the following lines for a sample", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex TemplateName = new(@"<xsl:template\s+name\s*=\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex ParamName = new(@"<xsl:param\s+name\s*=\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Setting = new(@"Setting\s*\[\s*@Name\s*=\s*[""']([^""']+)[""']\s*\]", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Element = new(@"<([A-Za-z_][\w.\-]*)[\s/>]", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex ExplicitElement = new(@"<xsl:element\s+name\s*=\s*[""']([^""'{]+)[""']", RegexOptions.CultureInvariant, Timeout);
    private static readonly Regex Branch = new(@"name\(\)\s*=\s*[""']([^""']+)[""']", RegexOptions.CultureInvariant, Timeout);

    private static readonly (string Token, string Hint)[] Markers =
    [
        ("xsl:for-each", "loops over source nodes (rebuild as a record link / repeat)"),
        ("position()", "uses the position of the current record (native: RecordPosition)"),
        ("xsl:choose", "branches on conditions (native: Conditional)"),
        ("xsl:if", "writes output only when a condition holds (native: condition on a field)"),
        ("xsl:call-template", "calls other named templates (each must be rebuilt too)"),
        ("xsl:variable", "keeps intermediate values in variables"),
        ("format-number", "formats numbers (check rounding against the native number functions)"),
        ("substring", "takes part of a string (native: StringSubstring)"),
        ("concat(", "concatenates text (native: StringConcat)"),
        ("translate(", "translates characters (native: StringReplace / case functions)"),
        ("normalize-space", "trims whitespace (native: StringTrim)"),
        ("sum(", "sums values (no verified native equivalent yet)"),
        ("count(", "counts nodes (BizTalk Count, FID 322, is not converted yet)"),
        ("document(", "reads other documents (cannot be a formula)"),
        ("msxsl:script", "embeds script code (cannot be a formula)"),
        ("xsl:key", "uses keyed lookups (no native equivalent)"),
        ("xsl:copy-of", "copies source nodes unchanged (no native equivalent yet)"),
        ("xsl:attribute", "writes attributes")
    ];

    /// <summary>True when the text is BizTalk's unedited default script template (which says "Uncomment the following lines for a sample ...").</summary>
    public static bool IsSample(string code)
    {
        try { return Sample.IsMatch(code); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static string? Template(string code) => First(TemplateName, code);

    public static IReadOnlyList<string> Parameters(string code) => All(ParamName, code, 16);

    public static IReadOnlyList<string> Describe(string language, string code)
    {
        var lines = new List<string>();
        var outputs = All(ExplicitElement, code, 12).Concat(All(Element, code, 12)).Distinct(StringComparer.Ordinal).Take(12).ToArray();
        if (outputs.Length > 0) lines.Add($"writes elements: {string.Join(", ", outputs)}");
        var settings = All(Setting, code, 16);
        if (settings.Count > 0) lines.Add($"reads configuration settings from the source message (ConfigurationInfo/AdditionalInfo/Setting): {string.Join(", ", settings)}");
        var branches = All(Branch, code, 12);
        if (branches.Count > 0) lines.Add($"branches on the source record name: {string.Join(", ", branches)}");
        foreach (var (token, hint) in Markers)
            if (code.Contains(token, StringComparison.Ordinal) && !lines.Contains(hint)) lines.Add(hint);
        return lines;
    }

    private static string? First(Regex regex, string code)
    {
        try { return regex.Match(code) is { Success: true } match ? match.Groups[1].Value : null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static List<string> All(Regex regex, string code, int max)
    {
        try { return regex.Matches(code).Select(match => match.Groups[1].Value).Distinct(StringComparer.Ordinal).Take(max).ToList(); }
        catch (RegexMatchTimeoutException) { return []; }
    }
}
