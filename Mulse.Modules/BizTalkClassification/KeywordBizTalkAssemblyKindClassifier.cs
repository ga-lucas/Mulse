namespace Mulse.Modules.BizTalkClassification;

/// <summary>
/// Built-in fallback classifier used by the BizTalk importer when no more specific
/// <see cref="IBizTalkAssemblyKindClassifier"/> matches. Matches the assembly name and source file names
/// against an ordered table of keyword-to-module-kind rules.
/// <para>
/// Contributors can add new rows here for common, widely-applicable BizTalk naming conventions (for example
/// another well-known custom pipeline component family). For organization-specific or proprietary naming,
/// register a separate <see cref="IBizTalkAssemblyKindClassifier"/> instead so it runs before this fallback
/// and doesn't need to touch shared, built-in rules.
/// </para>
/// </summary>
public sealed class KeywordBizTalkAssemblyKindClassifier : IBizTalkAssemblyKindClassifier
{
    private static readonly IReadOnlyList<(string ModuleKind, string[] Keywords)> Rules =
    [
        ("Fetch", ["retrieve", "receive", "fetch"]),
        ("Deliver", ["store", "send", "write", "dispatch"]),
        ("Render", ["xsl", "transform", "map"]),
        ("Parse", ["hl7", "flatfile", "parser", "typecaster"]),
    ];

    public string? TryClassify(BizTalkAssemblyClassificationContext context)
    {
        var haystack = string.Join(' ', SignalTokens(context)).ToLowerInvariant();
        foreach (var (moduleKind, keywords) in Rules)
        {
            if (keywords.Any(keyword => haystack.Contains(keyword, StringComparison.Ordinal)))
            {
                return moduleKind;
            }
        }

        return null;
    }

    private static IEnumerable<string> SignalTokens(BizTalkAssemblyClassificationContext context)
    {
        yield return context.AssemblyName;
        foreach (var sourceFileName in context.SourceFileNames)
        {
            yield return sourceFileName;
        }
    }
}
