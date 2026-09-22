using System.Text.RegularExpressions;
using Service.Models;

namespace Service.BizTalkImport;

/// <summary>
/// Best-effort extraction of promoted/written context properties from custom BizTalk pipeline component source
/// (e.g. <c>HL7Promotions.cs</c>), by regex-scanning for <c>*.Promote("Name", "namespace", value)</c> and
/// <c>*.Write("Name", "namespace", value)</c> call sites - the standard <c>Microsoft.BizTalk.Message.Interop
/// .IBaseMessageContext</c> APIs used to promote or write context properties during pipeline execution. This
/// never reproduces the component's real logic (which requires reading the message and deciding a value at
/// runtime); it only recovers *which* properties are promoted/written and under what namespace, so the importer
/// can pre-populate a <c>metadata-promotion-augment</c> step with the real property names instead of a single
/// generic guess, leaving the actual JSONPath/XPath selector for the user to fill in per property.
/// </summary>
internal static class BizTalkPromotedPropertyAnalyzer
{
    /// <summary>
    /// Matches <c>someContext.Promote("Name", "namespace", ...)</c> or <c>someContext.Write("Name", "namespace",
    /// ...)</c> call sites. The receiver is intentionally unconstrained (any identifier/member-access ending in
    /// <c>Promote</c>/<c>Write</c>) since components commonly use variable names like <c>context</c>,
    /// <c>pipelineContext</c>, or <c>pc</c>.
    /// </summary>
    private static readonly Regex PromoteOrWriteCallPattern = new(
        @"(?<method>[A-Za-z_][A-Za-z0-9_]*)\s*\.\s*(?<call>Promote|Write)\s*\(\s*""(?<name>(?:[^""\\]|\\.)*)""\s*,\s*""(?<namespace>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    /// <summary>
    /// Scans every given C# source file for <c>Promote</c>/<c>Write</c> context-property call sites. Never
    /// throws: unreadable files are skipped. Results are de-duplicated by (property name, namespace, call kind)
    /// across all files, since the same property is sometimes promoted from more than one code path.
    /// </summary>
    public static IReadOnlyList<BizTalkPromotedPropertyResponse> Analyze(IEnumerable<string> csFilePaths)
    {
        var results = new List<BizTalkPromotedPropertyResponse>();
        var seen = new HashSet<(string Name, string Namespace, bool IsPromoted)>();

        foreach (var filePath in csFilePaths)
        {
            string content;
            try
            {
                content = File.ReadAllText(filePath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (Match match in PromoteOrWriteCallPattern.Matches(content))
            {
                var propertyName = match.Groups["name"].Value;
                var propertyNamespace = match.Groups["namespace"].Value;
                if (string.IsNullOrWhiteSpace(propertyName))
                {
                    continue;
                }

                var isPromoted = string.Equals(match.Groups["call"].Value, "Promote", StringComparison.Ordinal);
                if (!seen.Add((propertyName, propertyNamespace, isPromoted)))
                {
                    continue;
                }

                results.Add(new BizTalkPromotedPropertyResponse(
                    propertyName,
                    string.IsNullOrWhiteSpace(propertyNamespace) ? null : propertyNamespace,
                    isPromoted,
                    Path.GetFileName(filePath)));
            }
        }

        return results;
    }

    /// <summary>
    /// Builds a <c>metadata-promotion-augment</c> <c>promotionsJson</c> array pre-populated with one entry per
    /// distinct extracted property (metadata key = camelCase property name), leaving <c>selector</c> as a
    /// clearly-marked TODO for the user to fill in with the real JSONPath/XPath once the payload shape imported
    /// alongside this flow is known. Returns null when no properties were found (caller falls back to its
    /// pre-existing generic placeholder).
    /// </summary>
    public static string? BuildPromotionsJson(IReadOnlyList<BizTalkPromotedPropertyResponse> properties)
    {
        if (properties.Count == 0)
        {
            return null;
        }

        var entries = properties
            .OrderBy(static property => property.PropertyName, StringComparer.OrdinalIgnoreCase)
            .Select(property =>
            {
                var metadataKey = ToCamelCase(property.PropertyName);
                var namespaceComment = property.Namespace is null ? string.Empty : $" ({property.Namespace})";
                return $$"""{"metadataKey":"{{EscapeJson(metadataKey)}}","selector":"TODO: selector for '{{EscapeJson(property.PropertyName)}}'{{EscapeJson(namespaceComment)}}"}""";
            });

        return $"[{string.Join(",", entries)}]";
    }

    private static string ToCamelCase(string value)
    {
        if (value.Length == 0 || char.IsLower(value[0]))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }

    private static string EscapeJson(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
