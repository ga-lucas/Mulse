using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Mulse.Modules.Orchestration;

public static partial class OdxProgramReader
{
    private static readonly XNamespace Designer = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    [GeneratedRegex(@"</(?:[A-Za-z_][\w.-]*:)?MetaModel\s*>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex EndTag();

    public static async Task<OdxProgram> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        using var file = new StreamReader(path, detectEncodingFromByteOrderMarks: true);
        var source = new System.Text.StringBuilder();
        var buffer = new char[8192];
        int read;
        while ((read = await file.ReadBlockAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (source.Length + read > 8_000_000) throw new ArgumentException("ODX exceeds the 8,000,000-character limit.");
            source.Append(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Read(source.ToString());
    }

    public static OdxProgram Read(string sourceText)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        if (sourceText.Length > 8_000_000) throw new ArgumentException("ODX exceeds the 8,000,000-character limit.");
        var start = sourceText.IndexOf("<?xml", StringComparison.Ordinal);
        var end = start < 0 ? Match.Empty : EndTag().Match(sourceText, start);
        if (!end.Success) throw new ArgumentException("ODX has no complete XML designer metadata section.");
        using var reader = XmlReader.Create(new StringReader(sourceText[start..(end.Index + end.Length)]), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 8_000_000
        });
        var document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        if (document.Root?.Name != Designer + "MetaModel") throw new ArgumentException("ODX designer namespace/root is not supported.");
        var all = document.Descendants(Designer + "Element").Take(10001).ToArray();
        if (all.Length is 0 or > 10000) throw new ArgumentException("ODX requires 1-10000 designer elements.");
        var ids = all.Select((element, index) => (element, id: index + 1)).ToDictionary(entry => entry.element, entry => entry.id);
        var offset = sourceText.AsSpan(0, start).Count('\n');
        OdxNode Convert(XElement element, int? parent, int depth)
        {
            if (depth > 64) throw new ArgumentException("ODX designer nesting exceeds 64 levels.");
            var properties = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in element.Elements(Designer + "Property"))
            {
                var name = (string?)property.Attribute("Name");
                if (string.IsNullOrWhiteSpace(name) || !properties.TryAdd(name, (string?)property.Attribute("Value") ?? ""))
                    throw new ArgumentException($"ODX element {ids[element]} has an empty or duplicate property name.");
            }
            var id = ids[element];
            return new(id, parent, ((IXmlLineInfo)element).LineNumber + offset,
                (string?)element.Attribute("OID") ?? "", (string?)element.Attribute("Type") ?? "Unknown",
                (string?)element.Attribute("ParentLink") ?? "", properties,
                element.Elements(Designer + "Element").Select(child => Convert(child, id, depth + 1)).ToArray());
        }
        var roots = document.Root.Elements(Designer + "Element").Select(element => Convert(element, null, 1)).ToArray();
        if (Count(roots) != all.Length) throw new ArgumentException("ODX has elements outside the supported direct parent/child hierarchy.");
        return new(1, roots);
    }

    private static int Count(IEnumerable<OdxNode> nodes) => nodes.Sum(node => 1 + Count(node.Children));
}
