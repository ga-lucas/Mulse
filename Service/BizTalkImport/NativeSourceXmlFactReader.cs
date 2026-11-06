using System.Text.RegularExpressions;
using Mulse.Modules.ControlFlow;

namespace Service.BizTalkImport;

internal static class NativeSourceXmlFactReader
{
    private static readonly Regex Function = new(@"\A\s*(?<kind>count|string|boolean)\((?<path>[\s\S]+)\)\s*\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Step = new("""\G(?<separator>//|/)(?<attribute>@)?(?:(?<simple>[A-Za-z_][A-Za-z_0-9.-]*)|\*\[\s*local-name\(\)\s*=\s*(?<quote>['"])(?<name>[^'"]+)\k<quote>\s+and\s+namespace-uri\(\)\s*=\s*(?<nsquote>['"])(?<namespace>[^'"]*)\k<nsquote>\s*\])(?:\[\s*@(?<filter>[A-Za-z_][A-Za-z_0-9.-]*)\s*=\s*(?<filterquote>['"])(?<filtervalue>[^'"]*)\k<filterquote>\s*\])?""", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static NativeMessageFact Read(string message, string xpath)
    {
        if (string.IsNullOrWhiteSpace(xpath) || xpath.Length > 4096) throw new NotSupportedException("Source XML facts require 1-4096 characters.");
        try
        {
            var function = Function.Match(xpath);
            if (!function.Success) throw new NotSupportedException("Only count/string/boolean of an absolute expanded-name XML path can become a native message fact.");
            var fact = new NativeMessageFact
            {
                MessageName = message,
                Kind = function.Groups["kind"].Value switch { "count" => NativeMessageFactKind.XmlCount, "string" => NativeMessageFactKind.XmlText, _ => NativeMessageFactKind.XmlExists }
            };
            var path = function.Groups["path"].Value;
            var offset = 0;
            while (offset < path.Length)
            {
                var match = Step.Match(path, offset);
                if (!match.Success || fact.Steps.Count >= 32) throw new NotSupportedException("XML facts require at most 32 absolute steps: child elements, one optional leading '//' descendant element, [@attribute='value'] filters and a terminal attribute; arbitrary XPath is not executed.");
                var descendant = match.Groups["separator"].Value == "//";
                var attribute = match.Groups["attribute"].Success;
                if (descendant && (offset != 0 || attribute) || attribute && match.Groups["filter"].Success)
                    throw new NotSupportedException("Only a leading '//name' element step and attribute filters on element steps are supported; arbitrary XPath is not executed.");
                fact.Steps.Add(new()
                {
                    Name = match.Groups["simple"].Success ? match.Groups["simple"].Value : match.Groups["name"].Value,
                    Namespace = match.Groups["namespace"].Value,
                    Attribute = attribute,
                    Descendant = descendant,
                    FilterAttribute = match.Groups["filter"].Value,
                    FilterValue = match.Groups["filtervalue"].Value
                });
                offset += match.Length;
            }
            fact.Validate();
            return fact;
        }
        catch (Exception exception) when (exception is ArgumentException or RegexMatchTimeoutException)
        { throw new NotSupportedException("Source XML fact is outside the bounded native expanded-name path contract.", exception); }
    }
}
