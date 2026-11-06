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
        "OptimizeValueMapping", "GenerateDefaultFixedNodes", "PreserveSequenceOrder", "IgnoreNamespacesForLinks", "method", "CopyPIs", "xmlVersion", "UseXSLTransform"
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
                "Link" => (new[] { "Links" }, new[] { "LinkID", "LinkFrom", "LinkTo", "Label" }),
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
        CheckSetting("UseXSLTransform", "Undefined", "No");
        CheckSetting("PreserveSequenceOrder", "No");
        var source = XmlSchemaInspector.Analyze(sourceSchemas, cancellationToken);
        var target = XmlSchemaInspector.Analyze(targetSchemas, cancellationToken);
        var sourceRoot = SelectRoot(source, "SrcTree");
        var targetRoot = SelectRoot(target, "TrgTree");
        var unsupportedTarget = targetRoot.Fields.FirstOrDefault(field => field.UnsupportedReason is not null || field.HasDefaultOrFixed);
        if (unsupportedTarget is not null)
            throw new ArgumentException($"Target '{unsupportedTarget.Path}' requires explicit translation: {unsupportedTarget.UnsupportedReason ?? "default/fixed values are not supported"}. No partial native map was produced.");
        var namespaces = source.Namespaces.ToDictionary(entry => "s" + entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var entry in target.Namespaces) namespaces.Add("t" + entry.Key, entry.Value);
        var links = new List<(XmlSchemaField From, XmlSchemaField To)>();
        foreach (var link in root.Descendants("Link"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (link.Parent?.Name != "Links") throw new ArgumentException("A map Link is outside a Links collection.");
            var from = Find(sourceRoot, (string?)link.Attribute("LinkFrom"));
            var to = Find(targetRoot, (string?)link.Attribute("LinkTo"));
            if (from.Scalar != to.Scalar)
                throw new ArgumentException($"Link '{from.Path}' -> '{to.Path}' connects a record to a scalar field. No partial native map was produced.");
            if (from.UnsupportedReason is not null || from.HasDefaultOrFixed)
                throw new ArgumentException($"Source '{from.Path}' is unsupported: {from.UnsupportedReason ?? "schema default/fixed values are not supported"}. No partial native map was produced.");
            links.Add((from, to));
        }
        var repeatLinks = new List<(XmlSchemaField From, XmlSchemaField To)>();
        foreach (var (from, to) in links)
        {
            if (!from.Scalar && from.Path == sourceRoot.Path && to.Path == targetRoot.Path) continue;
            if (from.Path == sourceRoot.Path || to.Path == targetRoot.Path)
                throw new ArgumentException($"Link '{from.Path}' -> '{to.Path}' connects a root to a non-root. No partial native map was produced.");
            if (from.SelfRepeating && !to.SelfRepeating)
                throw new ArgumentException($"Repeating source '{from.Path}' is linked to non-repeating target '{to.Path}'; BizTalk would create several elements where the schema allows one. No partial native map was produced.");
            if (from.Scalar && !from.SelfRepeating) continue;
            if (repeatLinks.Any(existing => existing.To == to))
                throw new ArgumentException($"Target '{to.Path}' has more than one record link.");
            repeatLinks.Add((from, to));
        }
        (XmlSchemaField From, XmlSchemaField To)? EnclosingRepeat(XmlSchemaField targetField)
            => repeatLinks.Where(repeat => targetField.Path == repeat.To.Path || targetField.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal))
                .OrderByDescending(repeat => repeat.To.Path.Length).Select(repeat => ((XmlSchemaField, XmlSchemaField)?)repeat).FirstOrDefault();
        void RequireRepeatCoverage(XmlSchemaField from, XmlSchemaField to)
        {
            foreach (var repeating in sourceRoot.Fields.Where(field => field.SelfRepeating && field.Path != from.Path
                         && from.Path.StartsWith(field.Path + "/", StringComparison.Ordinal)))
                if (!repeatLinks.Any(repeat => repeat.From == repeating && to.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal)))
                    throw new ArgumentException($"'{from.Path}' sits below repeating '{repeating.Path}', which has no record link enclosing target '{to.Path}'. No partial native map was produced.");
        }
        foreach (var (from, to) in repeatLinks) RequireRepeatCoverage(from, to);
        var assignments = new List<(XmlSchemaField Target, XmlFieldMappingDefinition Mapping)>();
        var optionalToRequired = new List<string>();
        foreach (var (from, to) in links.Where(link => link.From.Scalar))
        {
            RequireRepeatCoverage(from, to);
            var enclosing = EnclosingRepeat(to);
            var sourceScope = enclosing is { } scoped && from.Path.StartsWith(scoped.From.Path + "/", StringComparison.Ordinal) ? scoped.From.Path : sourceRoot.Path;
            if (enclosing is { } self && from.SelfRepeating && self.From == from) sourceScope = from.Path;
            var sourceRequired = RequiredWithin(sourceRoot, sourceScope, from);
            if (!sourceRequired && RequiredWithin(targetRoot, enclosing?.To.Path ?? targetRoot.Path, to))
                optionalToRequired.Add($"{from.Path} -> {to.Path}");
            assignments.Add((to, new()
            {
                SourcePath = Prefix(from.Path, "s"), TargetPath = TargetPath(to),
                MissingValueBehavior = sourceRequired ? MissingXmlValueBehavior.Default : MissingXmlValueBehavior.Omit,
                DefaultValue = sourceRequired ? string.Empty : null
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
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in assignments.Select(assignment => assignment.Target.Path).Concat(repeatLinks.Select(repeat => repeat.To.Path)))
            for (var current = path; current.Length > targetRoot.Path.Length; current = current[..current.LastIndexOf('/')]) produced.Add(current);
        foreach (var field in targetRoot.Fields.Where(field => field.Path != targetRoot.Path))
        {
            var isProduced = produced.Contains(field.Path);
            if (!isProduced && field.RequiredInParent)
                throw new ArgumentException($"Required target '{field.Path}' is not produced by any link or constant; the native engine cannot emit empty required records. No partial native map was produced.");
            if (isProduced && !field.Scalar && !field.RequiredInParent && !repeatLinks.Any(repeat => repeat.To == field))
                throw new ArgumentException($"Optional record '{field.Path}' would be created without a record link; BizTalk emits it only when a source exists. No partial native map was produced.");
        }
        foreach (var (_, to) in repeatLinks)
            if (!assignments.Any(assignment => assignment.Target.Path.StartsWith(to.Path + "/", StringComparison.Ordinal) || assignment.Target == to))
                throw new ArgumentException($"Record link to '{to.Path}' has no mapped fields below it; empty linked records are not supported. No partial native map was produced.");
        int Order(XmlSchemaField field) => targetRoot.Fields.TakeWhile(candidate => candidate != field).Count();
        var definition = new XmlMappingDefinition
        {
            Version = 2,
            SourceRoot = Prefix(sourceRoot.Path[1..], "s"),
            TargetRoot = Prefix(targetRoot.Path[1..], "t"), Namespaces = namespaces,
            SourceSchemas = sourceSchemas.ToArray(), TargetSchemas = targetSchemas.ToArray(),
            ValidateSource = false, ValidateTarget = false,
            Fields = assignments.OrderBy(assignment => Order(assignment.Target)).Select(assignment => assignment.Mapping).ToArray(),
            Repeats = repeatLinks.OrderBy(repeat => Order(repeat.To)).Select(repeat => new XmlRepeatMappingDefinition
            {
                SourcePath = Prefix(repeat.From.Path, "s"), TargetPath = Prefix(repeat.To.Path[(targetRoot.Path.Length + 1)..], "t")
            }).ToArray()
        };
        var json = JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var engine = new XmlMappingEngine(json);
        var warnings = new List<string>
        {
            "Review proposal only: scalar values, constants and record/repeat links were converted using the supplied schemas. No deployed policy association, BizTalk-equivalent output, or execution approval is established. Preview synthetic inputs and compare trusted expected outputs before using the map."
        };
        if (optionalToRequired.Count > 0)
            warnings.Add($"{optionalToRequired.Count} link(s) feed a required target from an optional source (for example {string.Join("; ", optionalToRequired.Take(3))}). Like BizTalk, the target element is omitted when the source is absent, so the output can then violate the target schema.");
        return new(json, engine.Xslt, warnings);

        bool RequiredWithin(XmlSchemaRoot tree, string scope, XmlSchemaField field)
            => tree.Fields.Where(candidate => candidate.Path.StartsWith(scope + "/", StringComparison.Ordinal)
                && (candidate.Path == field.Path || field.Path.StartsWith(candidate.Path + "/", StringComparison.Ordinal)))
                .All(candidate => candidate.RequiredInParent);
        void CheckSetting(string name, params string[] expected)
        {
            if (root.Attribute(name) is { } attribute && !expected.Contains(attribute.Value, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Map setting '{name}={attribute.Value}' is not supported by native map import.");
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
