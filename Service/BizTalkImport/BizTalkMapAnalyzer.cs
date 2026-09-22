using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Service.Models;

namespace Service.BizTalkImport;

/// <summary>
/// Best-effort translation of BizTalk map (.btm) files into an XSLT 1.0 stylesheet. Unlike orchestrations
/// (.odx), .btm files are well-formed XML on their own, so this parses the real file directly rather than
/// extracting an embedded designer-data region.
/// </summary>
/// <remarks>
/// A BizTalk map's <c>&lt;Link LinkFrom="..." LinkTo="..."/&gt;</c> and <c>&lt;Value value="..." Query="..."/&gt;</c>
/// elements express field-to-field assignments and literal constants as XPath 1.0 expressions - both endpoints
/// always start with a sentinel segment <c>/*[local-name()='&lt;Schema&gt;']</c> (the literal placeholder text
/// "&lt;Schema&gt;", not a real element name) meaning "the document's root element, whatever it's actually
/// named". Everything after that sentinel is a real XPath path built from <c>*[local-name()='X']</c> (element)
/// or <c>@*[local-name()='X']</c> (attribute) segments. This analyzer strips the sentinel, walks all target
/// (<c>LinkTo</c>/<c>Query</c>) paths into a tree, and emits a literal-result-element XSLT template that walks
/// the same tree: attribute leaves become attribute-value-template assignments; element leaves become a
/// shallow content copy of the mapped source node's attributes and children; container elements (paths with
/// further nested links beneath them) become plain nested wrapper elements.
///
/// Any <c>LinkFrom</c>/<c>LinkTo</c>/<c>Query</c> value that doesn't start with a real XPath segment references
/// a BizTalk functoid (transformation logic) by ID instead of a field - those can't be safely guessed, so
/// they're counted as untranslated and called out in a TODO comment rather than silently dropped without a
/// trace. This never blocks or fails import: any parse failure for a given map silently degrades to a null
/// result, and the caller falls back to its pre-existing xslt-render placeholder behavior.
/// </remarks>
internal static class BizTalkMapAnalyzer
{
    private static readonly XNamespace XslNamespace = "http://www.w3.org/1999/XSL/Transform";
    private const string SchemaRootSentinel = "/*[local-name()='<Schema>']";
    private static readonly Regex SegmentPattern = new(@"^@?\*\[local-name\(\)='(?<name>[^']+)'\]$", RegexOptions.Compiled);

    /// <summary>
    /// Analyzes a single .btm map file. Never throws: returns null if the file can't be read/parsed, or if it
    /// has no translatable links/constants at all (a purely functoid-driven map, for example).
    /// </summary>
    public static BizTalkMapAnalysisResponse? Analyze(string mapFilePath, string mapName)
    {
        try
        {
            if (!File.Exists(mapFilePath))
            {
                return null;
            }

            var document = XDocument.Load(mapFilePath, LoadOptions.None);
            var root = document.Root;
            if (root is null || !string.Equals(root.Name.LocalName, "mapsource", StringComparison.Ordinal))
            {
                return null;
            }

            var srcTree = root.Elements().FirstOrDefault(static e => string.Equals(e.Name.LocalName, "SrcTree", StringComparison.Ordinal));
            var trgTree = root.Elements().FirstOrDefault(static e => string.Equals(e.Name.LocalName, "TrgTree", StringComparison.Ordinal));
            var sourceSchemaRef = srcTree?.Elements()
                .FirstOrDefault(static e => string.Equals(e.Name.LocalName, "Reference", StringComparison.Ordinal))?
                .Attribute("Location")?.Value ?? "(unknown source schema)";
            var targetSchemaRef = trgTree?.Elements()
                .FirstOrDefault(static e => string.Equals(e.Name.LocalName, "Reference", StringComparison.Ordinal))?
                .Attribute("Location")?.Value ?? "(unknown target schema)";
            var targetRootName = trgTree?.Attribute("RootNode_Name")?.Value;
            if (string.IsNullOrWhiteSpace(targetRootName))
            {
                targetRootName = "Root";
            }

            var rootNode = new MapTargetNode();
            var directCount = 0;
            var functoidCount = 0;

            var links = document.Descendants().Where(static e => string.Equals(e.Name.LocalName, "Link", StringComparison.Ordinal));
            foreach (var link in links)
            {
                var linkFrom = link.Attribute("LinkFrom")?.Value;
                var linkTo = link.Attribute("LinkTo")?.Value;
                if (string.IsNullOrWhiteSpace(linkFrom) || string.IsNullOrWhiteSpace(linkTo)
                    || !TryCleanXPath(linkFrom, out var cleanedSource)
                    || !TryInsertTargetAssignment(rootNode, linkTo, new MapLeafAssignment(cleanedSource, null)))
                {
                    functoidCount++;
                    continue;
                }

                directCount++;
            }

            var constantValues = document.Descendants()
                .Where(static e => string.Equals(e.Name.LocalName, "Value", StringComparison.Ordinal)
                    && string.Equals(e.Parent?.Name.LocalName, "ConstantValues", StringComparison.Ordinal));
            var constantCount = 0;
            foreach (var value in constantValues)
            {
                var query = value.Attribute("Query")?.Value;
                var text = value.Attribute("value")?.Value ?? string.Empty;
                if (string.IsNullOrWhiteSpace(query) || !TryInsertTargetAssignment(rootNode, query, new MapLeafAssignment(null, text)))
                {
                    functoidCount++;
                    continue;
                }

                constantCount++;
            }

            if (directCount == 0 && constantCount == 0)
            {
                // Nothing translatable (e.g. a purely functoid-driven map) - an empty stylesheet wouldn't help.
                return new BizTalkMapAnalysisResponse(mapName, mapFilePath, sourceSchemaRef, targetSchemaRef, 0, 0, functoidCount, null, null, null);
            }

            var xslt = GenerateXslt(mapName, sourceSchemaRef, targetSchemaRef, targetRootName, rootNode, directCount, constantCount, functoidCount);
            var fileName = $"{SanitizeFileNameSegment(mapName)}.xslt";
            var moduleId = $"migrated-map-{CreateSlug(mapName)}";

            return new BizTalkMapAnalysisResponse(mapName, mapFilePath, sourceSchemaRef, targetSchemaRef, directCount, constantCount, functoidCount, fileName, moduleId, xslt);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>
    /// Strips the BizTalk Mapper's "document root, whatever it's named" sentinel segment
    /// (<see cref="SchemaRootSentinel"/>) from the start of an XPath, replacing it with a literal <c>/*</c>
    /// wildcard-root step. Returns false (rather than guessing) for anything that isn't a real XPath at all -
    /// i.e. a bare functoid ID reference, which BizTalk also stores in <c>LinkFrom</c>/<c>LinkTo</c>/<c>Query</c>.
    /// </summary>
    private static bool TryCleanXPath(string raw, out string cleaned)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith(SchemaRootSentinel, StringComparison.Ordinal))
        {
            cleaned = "/*" + trimmed[SchemaRootSentinel.Length..];
            return true;
        }

        if (trimmed.StartsWith("/*[local-name()=", StringComparison.Ordinal) || trimmed.StartsWith("/@*[local-name()=", StringComparison.Ordinal))
        {
            cleaned = trimmed;
            return true;
        }

        cleaned = string.Empty;
        return false;
    }

    /// <summary>
    /// Parses a cleaned target (<c>LinkTo</c>/<c>Query</c>) XPath into path segments and inserts the
    /// assignment into the target tree at that path, creating intermediate element nodes as needed. Drops the
    /// leading sentinel-derived root wildcard segment because it represents the document root already modeled
    /// by the generated stylesheet's own root wrapper element, not a literal nested element. Returns false
    /// (without partially mutating the tree in a way that matters) if any segment doesn't match the expected
    /// <c>*[local-name()='X']</c>/<c>@*[local-name()='X']</c> shape, or if an attribute segment appears
    /// anywhere but last - both indicate a functoid-involved or otherwise unsupported path.
    /// </summary>
    private static bool TryInsertTargetAssignment(MapTargetNode root, string rawTargetPath, MapLeafAssignment assignment)
    {
        if (!TryCleanXPath(rawTargetPath, out var cleaned))
        {
            return false;
        }

        var rawSegments = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
        {
            return false;
        }

        var startIndex = rawSegments[0] == "*" ? 1 : 0;
        if (startIndex >= rawSegments.Length)
        {
            return false;
        }

        var segments = new List<(string Name, bool IsAttribute)>();
        for (var i = startIndex; i < rawSegments.Length; i++)
        {
            var segment = rawSegments[i];
            var match = SegmentPattern.Match(segment);
            if (!match.Success)
            {
                return false;
            }

            segments.Add((match.Groups["name"].Value, segment.StartsWith('@')));
        }

        var current = root;
        for (var i = 0; i < segments.Count; i++)
        {
            var (name, isAttribute) = segments[i];
            var isLast = i == segments.Count - 1;
            if (isAttribute)
            {
                if (!isLast)
                {
                    return false;
                }

                current.Attributes[name] = assignment;
                return true;
            }

            if (!current.Elements.TryGetValue(name, out var child))
            {
                child = new MapTargetNode();
                current.Elements[name] = child;
            }

            current = child;
            if (isLast)
            {
                current.OwnAssignment = assignment;
            }
        }

        return true;
    }

    private static string GenerateXslt(
        string mapName,
        string sourceSchemaRef,
        string targetSchemaRef,
        string targetRootElementName,
        MapTargetNode rootNode,
        int directCount,
        int constantCount,
        int functoidCount)
    {
        var targetElement = RenderElement(targetRootElementName, rootNode);
        var stylesheet = new XElement(
            XslNamespace + "stylesheet",
            new XAttribute(XNamespace.Xmlns + "xsl", XslNamespace.NamespaceName),
            new XAttribute("version", "1.0"),
            new XElement(XslNamespace + "output", new XAttribute("method", "xml"), new XAttribute("indent", "yes")),
            new XElement(XslNamespace + "template", new XAttribute("match", "/"), targetElement));

        var header = new StringBuilder();
        header.AppendLine("<!--");
        header.AppendLine($"  Auto-generated by Mulse's BizTalk map importer from '{mapName}.btm'. Review before enabling.");
        header.AppendLine($"  Source schema: {sourceSchemaRef}");
        header.AppendLine($"  Target schema: {targetSchemaRef} (root element '{targetRootElementName}')");
        header.AppendLine($"  {directCount} direct link(s) and {constantCount} constant value(s) were translated automatically below.");
        if (functoidCount > 0)
        {
            header.AppendLine($"  TODO: {functoidCount} link(s)/value(s) reference BizTalk functoids (transformation logic, e.g. string/math/looping functoids) and could NOT be translated - they are omitted here. Re-add the missing target field(s) by hand, using the original .btm map (opened in a BizTalk Mapper-compatible tool) as reference.");
        }

        header.AppendLine("  TODO: BizTalk map links ignore namespaces (IgnoreNamespacesForLinks=\"Yes\"), so every element below is generated without a namespace. If the target schema is namespace-qualified, add the correct xmlns declaration(s) before using this in production.");
        if (string.Equals(sourceSchemaRef, targetSchemaRef, StringComparison.OrdinalIgnoreCase))
        {
            header.AppendLine("  TODO: Source and target reference the same schema (a self-map, e.g. re-shaping fields within one message type). Compare the generated root element against a real sample message - if the schema's own root element name matches the wrapper below, this stylesheet may double-nest it; remove the extra level if so.");
        }
        header.AppendLine("-->");

        return header.ToString() + stylesheet.ToString(SaveOptions.None);
    }

    private static XElement RenderElement(string name, MapTargetNode node)
    {
        var element = new XElement(name);
        foreach (var (attributeName, leaf) in node.Attributes)
        {
            element.SetAttributeValue(attributeName, RenderScalarValue(leaf));
        }

        if (node.Elements.Count > 0)
        {
            // A container: any leaf assignment landing directly on this node is a redundant grouping link in
            // the original map (BizTalk's mapper draws these when a parent record itself has a line connected
            // to it even though its children are separately, more specifically, mapped) - the deeper, more
            // specific child assignments always take precedence.
            foreach (var (childName, childNode) in node.Elements)
            {
                element.Add(RenderElement(childName, childNode));
            }
        }
        else if (node.OwnAssignment is { } assignment)
        {
            if (assignment.ConstantText is not null)
            {
                element.Add(assignment.ConstantText);
            }
            else if (assignment.SourceXPath is not null)
            {
                // Shallow content copy: copies the mapped source element's own attributes and children, but
                // not the source element's own tag (which would wrongly double-nest it inside the target
                // wrapper element already built above).
                element.Add(new XElement(XslNamespace + "copy-of", new XAttribute("select", $"{assignment.SourceXPath}/@*")));
                element.Add(new XElement(XslNamespace + "copy-of", new XAttribute("select", $"{assignment.SourceXPath}/node()")));
            }
        }

        return element;
    }

    private static string RenderScalarValue(MapLeafAssignment leaf)
    {
        if (leaf.ConstantText is not null)
        {
            // Escape any literal curly braces so the XSLT processor doesn't mistake them for the start of an
            // attribute value template expression.
            return leaf.ConstantText.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
        }

        return $"{{{leaf.SourceXPath}}}";
    }

    private static string SanitizeFileNameSegment(string value)
    {
        var cleaned = Regex.Replace(value.Trim(), "[^A-Za-z0-9]+", string.Empty);
        return cleaned.Length == 0 ? "MigratedMap" : cleaned;
    }

    private static string CreateSlug(string value)
    {
        var spaced = Regex.Replace(value.Trim(), "(?<=[a-z0-9])(?=[A-Z])", "-");
        var lowered = spaced.ToLowerInvariant();
        var slug = Regex.Replace(lowered, "[^a-z0-9]+", "-").Trim('-');
        return slug.Length == 0 ? "map" : slug;
    }

    private sealed class MapTargetNode
    {
        public Dictionary<string, MapTargetNode> Elements { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, MapLeafAssignment> Attributes { get; } = new(StringComparer.Ordinal);

        public MapLeafAssignment? OwnAssignment { get; set; }
    }

    private sealed record MapLeafAssignment(string? SourceXPath, string? ConstantText);
}
