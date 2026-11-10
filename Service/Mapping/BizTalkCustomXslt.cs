using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Service.Mapping;

/// <summary>A BizTalk map that delegates to a hand-written stylesheet (CustomXSLT); BizTalk ignores such a map's grid and links.</summary>
internal sealed record BizTalkCustomXsltMap(string StylesheetPath, string StylesheetText, bool HasScripts, IReadOnlyList<string> ScriptMethods, IReadOnlyList<string> ScriptCalls);

internal static partial class BizTalkCustomXslt
{
    public const int MaximumStylesheetBytes = 1_048_576;
    private static readonly XNamespace Xsl = "http://www.w3.org/1999/XSL/Transform";
    private static readonly XNamespace Msxsl = "urn:schemas-microsoft-com:xslt";

    [GeneratedRegex(@"public\s+[\w<>\.\[\]]+\s+(\w+)\s*\(")]
    private static partial Regex MethodPattern();

    /// <summary>Returns null when the map does not use CustomXSLT. Throws ArgumentException when it does but the stylesheet cannot be used as is.</summary>
    public static BizTalkCustomXsltMap? Read(XDocument map, string mapPath)
    {
        var custom = map.Root?.Elements("CustomXSLT").FirstOrDefault();
        if (custom is null) return null;
        if (!string.IsNullOrWhiteSpace((string?)custom.Attribute("ExtObjXmlPath")))
            throw new ArgumentException("The map's custom XSLT uses an extension-object file (ExtObjXmlPath), which needs an external assembly; port that logic into the stylesheet or a native module.");
        var relative = ((string?)custom.Attribute("XsltPath"))?.Trim();
        if (string.IsNullOrEmpty(relative)) throw new ArgumentException("The map's CustomXSLT element has no XsltPath.");
        var directory = Path.GetDirectoryName(Path.GetFullPath(mapPath))!;
        var path = Path.GetFullPath(Path.Combine(directory, relative.Replace('\\', Path.DirectorySeparatorChar)));
        if (!File.Exists(path)) throw new ArgumentException($"The custom stylesheet '{relative}' was not found next to the map; add it to the analyzed sources.");
        if (new FileInfo(path).Length > MaximumStylesheetBytes) throw new ArgumentException($"The custom stylesheet '{relative}' exceeds {MaximumStylesheetBytes} bytes.");
        XDocument stylesheet;
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            stylesheet = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new ArgumentException($"The custom stylesheet '{relative}' is not well-formed XML: {exception.Message}");
        }
        var root = stylesheet.Root;
        if (root is null || root.Name.Namespace != Xsl || root.Name.LocalName is not ("stylesheet" or "transform"))
            throw new ArgumentException($"The custom stylesheet '{relative}' is not an xsl:stylesheet or xsl:transform document.");
        if (((string?)root.Attribute("version"))?.Trim() != "1.0")
            throw new ArgumentException($"The custom stylesheet '{relative}' is not XSLT 1.0; BizTalk's engine only runs 1.0.");
        var scriptBlocks = root.Descendants(Msxsl + "script").Select(static script => script.Value).ToArray();
        var methods = scriptBlocks.SelectMany(static code => MethodPattern().Matches(code).Select(static match => match.Groups[1].Value)).Distinct(StringComparer.Ordinal).ToArray();
        var calls = scriptBlocks.Length == 0 ? [] : methods
            .Where(method => root.Descendants().Attributes().Any(attribute => attribute.Value.Contains(method + "(", StringComparison.Ordinal)))
            .ToArray();
        return new(path, stylesheet.ToString(SaveOptions.DisableFormatting), scriptBlocks.Length > 0, methods, calls);
    }
}
