using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Mapping;

public sealed record NativeMapConversion(string MappingJson, string Xslt, IReadOnlyList<string> Warnings);

public static class BizTalkNativeMapConverter
{
    private const string Sentinel = "/*[local-name()='<Schema>']";
    private static readonly Regex Segment = new(@"^(@?)\*\[local-name\(\)='([^']+)'\]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "mapsource", "SrcTree", "TrgTree", "Reference", "TreeValues", "TestValues", "ConstantValues", "Value",
        "Pages", "Page", "Links", "Link", "Functoids", "ScriptTypePrecedence", "CSharp", "ExternalAssembly", "VbNet", "JScript", "XsltCallTemplate", "Xslt"
    };
    private static readonly HashSet<string> MetadataAttributes = new(StringComparer.Ordinal)
    {
        "Name", "Version", "BizTalkServerMapperTool_Version", "XRange", "YRange", "OmitXmlDeclaration", "TreatElementsAsRecords",
        "OptimizeValueMapping", "GenerateDefaultFixedNodes", "PreserveSequenceOrder", "IgnoreNamespacesForLinks", "method", "CopyPIs", "xmlVersion"
    };

    public static NativeMapConversion Convert(string mapXml, IReadOnlyList<string> sourceSchemas, IReadOnlyList<string> targetSchemas,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = XmlMappingSchemas.ReadXml(mapXml);
        var root = document.Root!;
        if (root.Name != "mapsource") throw new ArgumentException("Supply a BizTalk mapsource (.btm) document.");
        var unknown = root.DescendantsAndSelf().FirstOrDefault(element => element.Name.NamespaceName.Length != 0 || !Elements.Contains(element.Name.LocalName));
        if (unknown is not null)
            throw new ArgumentException($"Unsupported map construct '{unknown.Name}'. No partial native map was produced; scripts and functoids require explicit translation.");
        if (root.Attributes().Any(attribute => !MetadataAttributes.Contains(attribute.Name.LocalName) || attribute.Name.NamespaceName.Length != 0))
            throw new ArgumentException("Unsupported map-level setting. Custom XSLT and unknown map settings cannot be imported as a scalar map.");
        foreach (var element in root.Descendants())
        {
            var (parents, attributes) = element.Name.LocalName switch
            {
                "SrcTree" or "TrgTree" => (new[] { "mapsource" }, new[] { "RootNode_Name" }),
                "Reference" => (new[] { "SrcTree", "TrgTree" }, new[] { "Location" }),
                "TreeValues" or "Pages" or "ScriptTypePrecedence" => (new[] { "mapsource" }, Array.Empty<string>()),
                "TestValues" or "ConstantValues" => (new[] { "TreeValues" }, Array.Empty<string>()),
                "Page" => (new[] { "Pages" }, new[] { "Name" }),
                "Links" or "Functoids" => (new[] { "Page" }, Array.Empty<string>()),
                "Link" => (new[] { "Links" }, new[] { "LinkFrom", "LinkTo", "Label" }),
                "Value" => (new[] { "TestValues", "ConstantValues" }, new[] { "Query", "value" }),
                _ => (new[] { "ScriptTypePrecedence" }, new[] { "Enabled" })
            };
            if (!parents.Contains(element.Parent!.Name.LocalName, StringComparer.Ordinal)
                || element.Attributes().Any(attribute => attribute.Name.NamespaceName.Length != 0 || !attributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                || element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)))
                throw new ArgumentException($"Unsupported content, placement, or setting on '{element.Name}'. No partial native map was produced.");
        }
        if (root.Attribute("Version") is { Value: not ("1" or "2") })
            throw new ArgumentException("Unsupported BizTalk map format version.");
        CheckSetting("method", "xml");
        CheckSetting("CopyPIs", "No");
        CheckSetting("xmlVersion", "1.0");
        CheckSetting("TreatElementsAsRecords", "No");
        CheckSetting("OmitXmlDeclaration", "Yes");
        var source = XmlSchemaInspector.Analyze(sourceSchemas, cancellationToken);
        var target = XmlSchemaInspector.Analyze(targetSchemas, cancellationToken);
        var sourceRoot = SelectRoot(source, "SrcTree");
        var targetRoot = SelectRoot(target, "TrgTree");
        var unsupportedTarget = targetRoot.Fields.FirstOrDefault(field => field.UnsupportedReason is not null || field.Repeating || field.HasDefaultOrFixed
            || (!field.Scalar && (!field.Required || !targetRoot.Fields.Any(child => child.Scalar && child.Path.StartsWith(field.Path + "/", StringComparison.Ordinal)))));
        if (unsupportedTarget is not null)
            throw new ArgumentException($"Target '{unsupportedTarget.Path}' requires explicit translation: {unsupportedTarget.UnsupportedReason ?? "optional/empty records, repetitions, or default/fixed values are not supported"}. No partial native map was produced.");
        var namespaces = source.Namespaces.ToDictionary(entry => "s" + entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var entry in target.Namespaces) namespaces.Add("t" + entry.Key, entry.Value);
        var assignments = new List<(XmlSchemaField Target, XmlFieldMappingDefinition Mapping)>();
        foreach (var link in root.Descendants("Link"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (link.Parent?.Name != "Links") throw new ArgumentException("A map Link is outside a Links collection.");
            var from = Find(sourceRoot, (string?)link.Attribute("LinkFrom"));
            var to = Find(targetRoot, (string?)link.Attribute("LinkTo"));
            if (!from.Scalar || from.Repeating || from.HasDefaultOrFixed || from.UnsupportedReason is not null || (!from.Required && to.Required))
                throw new ArgumentException($"Source '{from.Path}' must be a non-repeating scalar without defaults; optional sources need optional targets. No partial native map was produced.");
            assignments.Add((to, new()
            {
                SourcePath = Prefix(from.Path, "s"), TargetPath = TargetPath(to),
                MissingValueBehavior = from.Required ? MissingXmlValueBehavior.Error : MissingXmlValueBehavior.Omit
            }));
        }
        foreach (var value in root.Descendants("ConstantValues").Elements())
        {
            if (value.Name != "Value" || value.Attribute("value") is null)
                throw new ArgumentException("Unsupported constant value construct.");
            var to = Find(targetRoot, (string?)value.Attribute("Query"));
            assignments.Add((to, new() { Constant = value.Attribute("value")!.Value, TargetPath = TargetPath(to) }));
        }
        if (root.Descendants("Value").Any(value => value.Parent?.Name != "ConstantValues" && value.Parent?.Name != "TestValues"))
            throw new ArgumentException("A Value is outside the supported ConstantValues/TestValues collections.");
        if (targetRoot.Fields.Any(field => field.Required && field.Scalar && field.Path != targetRoot.Path && !assignments.Any(assignment => assignment.Target == field)))
            throw new ArgumentException("The map leaves required target scalar fields unmapped. No partial native map was produced.");
        var definition = new XmlMappingDefinition
        {
            Version = 2,
            SourceRoot = Prefix(sourceRoot.Path[1..], "s"),
            TargetRoot = Prefix(targetRoot.Path[1..], "t"), Namespaces = namespaces,
            SourceSchemas = sourceSchemas.ToArray(), TargetSchemas = targetSchemas.ToArray(),
            Fields = assignments.OrderBy(assignment => targetRoot.Fields.TakeWhile(field => field != assignment.Target).Count()).Select(assignment => assignment.Mapping).ToArray()
        };
        var json = JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var engine = new XmlMappingEngine(json);
        return new(json, engine.Xslt,
        ["Review proposal only: scalar values and constants were converted using the supplied schemas. No deployed policy association, BizTalk-equivalent output, or execution approval is established. Preview synthetic inputs and compare trusted expected outputs before using the map."]);

        void CheckSetting(string name, string expected)
        {
            if (root.Attribute(name) is { } attribute && !string.Equals(attribute.Value, expected, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"Map setting '{name}={attribute.Value}' is not supported by native scalar import.");
        }
        XmlSchemaRoot SelectRoot(XmlSchemaTree tree, string name)
        {
            var trees = root.Elements(name).ToArray();
            if (trees.Length != 1 || string.IsNullOrWhiteSpace((string?)trees[0].Attribute("RootNode_Name")))
                throw new ArgumentException($"The map must declare exactly one {name} with a RootNode_Name.");
            var rootName = trees[0].Attribute("RootNode_Name")!.Value;
            var matches = tree.Roots.Where(candidate => candidate.Fields[0].LocalPath == "/" + rootName).ToArray();
            return matches.Length == 1 ? matches[0] : throw new ArgumentException($"Map root '{rootName}' must identify exactly one supplied XSD global element; found {matches.Length}.");
        }
        string TargetPath(XmlSchemaField field)
        {
            if (!field.Scalar || field.Path == targetRoot.Path)
                throw new ArgumentException($"Target '{field.Path}' is a record or root value, not a supported scalar field.");
            return Prefix(field.Path[(targetRoot.Path.Length + 1)..], "t");
        }
    }

    private static XmlSchemaField Find(XmlSchemaRoot root, string? expression)
    {
        if (expression is null || !expression.StartsWith(Sentinel, StringComparison.Ordinal))
            throw new ArgumentException("A link/constant uses an unsupported XPath or functoid reference. No partial native map was produced.");
        var segments = expression[Sentinel.Length..].Split('/', StringSplitOptions.None);
        if (segments.Length < 2 || segments[0].Length != 0)
            throw new ArgumentException("A link must address an explicit schema root and scalar field.");
        var names = segments.Skip(1).Select(part =>
        {
            var match = Segment.Match(part);
            if (!match.Success) throw new ArgumentException($"Unsupported BizTalk link segment '{part}'.");
            return match.Groups[1].Value + match.Groups[2].Value;
        });
        var path = "/" + string.Join("/", names);
        var matches = root.Fields.Where(field => field.LocalPath == path).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException($"Schema path '{path}' is missing or namespace-ambiguous; no local-name guess was used.");
    }

    private static string Prefix(string path, string side)
        => string.Join("/", path.Split('/').Select(segment => segment.Contains(':') && !segment.StartsWith("xml:", StringComparison.Ordinal)
            && !segment.StartsWith("@xml:", StringComparison.Ordinal)
                ? segment.StartsWith('@') ? "@" + side + segment[1..] : side + segment
                : segment));
}
