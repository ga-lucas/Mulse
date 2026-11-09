using System.Globalization;
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
        "Pages", "Page", "Links", "Link", "Functoids", "ScriptTypePrecedence", "CSharp", "ExternalAssembly", "VbNet", "JScript", "XsltCallTemplate", "Xslt",
        "Functoid", "Input-Parameters", "Parameter", "ScripterCode", "Script"
    };

    /// <summary>Functoids whose BizTalk helper bodies were verified against compiled maps (*.btm.cs). Anything else stays unconverted and is named in the error.</summary>
    private static readonly IReadOnlyDictionary<int, XmlMappingFunction> FunctoidCatalog = new Dictionary<int, XmlMappingFunction>
    {
        [102] = XmlMappingFunction.StringLeft, [104] = XmlMappingFunction.StringRight, [105] = XmlMappingFunction.StringSize,
        [107] = XmlMappingFunction.StringConcat, [110] = XmlMappingFunction.StringUpperCase, [112] = XmlMappingFunction.MathInt,
        [121] = XmlMappingFunction.MathDivide, [123] = XmlMappingFunction.DateCurrentDate, [125] = XmlMappingFunction.DateCurrentDateTime,
        [311] = XmlMappingFunction.LogicalGt, [315] = XmlMappingFunction.LogicalEq, [316] = XmlMappingFunction.LogicalNe,
        [321] = XmlMappingFunction.LogicalAnd, [701] = XmlMappingFunction.LogicalExistence
    };
    private static readonly HashSet<string> MetadataAttributes = new(StringComparer.Ordinal)
    {
        "Name", "Version", "BizTalkServerMapperTool_Version", "XRange", "YRange", "OmitXmlDeclaration", "TreatElementsAsRecords",
        "OptimizeValueMapping", "GenerateDefaultFixedNodes", "PreserveSequenceOrder", "IgnoreNamespacesForLinks", "method", "CopyPIs", "xmlVersion", "UseXSLTransform"
    };

    /// <param name="embedSchemas">Persist the XSD documents in the mapping (default). Disable for compact derived choices; the persisted map never validates against them either way, and keeps its source-root assertion.</param>
    public static NativeMapConversion Convert(string mapXml, IReadOnlyList<string> sourceSchemas, IReadOnlyList<string> targetSchemas,
        CancellationToken cancellationToken = default, bool embedSchemas = true)
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
                "Functoid" => (new[] { "Functoids" }, new[] { "FunctoidID", "Functoid-FID", "X-Cell", "Y-Cell", "Label", "Comments", "Functoid-Name" }),
                "Input-Parameters" or "ScripterCode" => (new[] { "Functoid" }, Array.Empty<string>()),
                "Parameter" => (new[] { "Input-Parameters" }, new[] { "Type", "Value", "Guid", "linkIndex" }),
                "Script" => (new[] { "ScripterCode" }, new[] { "Language" }),
                _ => (new[] { "ScriptTypePrecedence" }, new[] { "Enabled" })
            };
            if (!parents.Contains(element.Parent!.Name.LocalName, StringComparer.Ordinal)
                || element.Attributes().Any(attribute => attribute.Name.NamespaceName.Length != 0 || !attributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal))
                || (element.Name.LocalName != "Script" && element.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value))))
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
        var functoids = ReadFunctoids(root);
        var functoidOutputs = new List<(int Functoid, XmlSchemaField To)>();
        foreach (var link in root.Descendants("Link"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (link.Parent?.Name != "Links") throw new ArgumentException("A map Link is outside a Links collection.");
            var fromText = (string?)link.Attribute("LinkFrom");
            var toText = (string?)link.Attribute("LinkTo");
            var toFunctoid = FunctoidReference(toText, functoids);
            var fromFunctoid = FunctoidReference(fromText, functoids);
            if (toFunctoid is not null) continue; // input wiring is resolved from the functoid's own parameters
            if (fromFunctoid is { } producer)
            {
                var output = Find(targetRoot, toText);
                if (!output.Scalar || output.Path == targetRoot.Path)
                    throw new ArgumentException($"Functoid {producer} is linked to record '{output.Path}'; conditional record creation is not supported. No partial native map was produced.");
                functoidOutputs.Add((producer, output));
                continue;
            }
            var from = Find(sourceRoot, fromText);
            var to = Find(targetRoot, toText);
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
        var resolver = new FunctoidResolver(functoids, root.Descendants("Link").ToArray(), sourceRoot);
        foreach (var (producer, to) in functoidOutputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = resolver.Resolve(producer);
            foreach (var field in resolved.Sources) RequireRepeatCoverage(field, to);
            assignments.Add((to, new() { Expression = resolved.Expression, TargetPath = TargetPath(to) }));
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
            SourceSchemas = embedSchemas ? sourceSchemas.ToArray() : [], TargetSchemas = embedSchemas ? targetSchemas.ToArray() : [],
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
        if (resolver.Used.Count > 0)
            warnings.Add($"Functoid formulas were converted for {functoidOutputs.Count} target(s) (FID {string.Join(", ", resolver.Used.Order())}). Their behavior ports the helper code BizTalk compiled into other maps; it has not been compared with this map's compiled output. Current-date functions use the Mulse host's local time. Compare trusted expected outputs before use.");
        if (functoids.Count > 0 && resolver.Contributing.Count < functoids.Count)
            warnings.Add($"{functoids.Count - resolver.Contributing.Count} functoid(s) (for example unconnected sample script stubs) are not connected to any target and were ignored, as BizTalk generates no output for them.");
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

    private sealed record Functoid(int Id, int Fid, XElement Element);

    private sealed record ResolvedFunctoid(XmlValueExpression Expression, IReadOnlyList<XmlSchemaField> Sources, int Nodes, int Depth);

    private static Dictionary<int, Functoid> ReadFunctoids(XElement root)
    {
        var result = new Dictionary<int, Functoid>();
        foreach (var element in root.Descendants("Functoid"))
        {
            if (!int.TryParse((string?)element.Attribute("FunctoidID"), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse((string?)element.Attribute("Functoid-FID"), NumberStyles.None, CultureInfo.InvariantCulture, out var fid))
                throw new ArgumentException("A functoid has a missing or invalid FunctoidID or Functoid-FID. No partial native map was produced.");
            if (!result.TryAdd(id, new Functoid(id, fid, element)))
                throw new ArgumentException($"FunctoidID {id} is declared more than once. No partial native map was produced.");
            if (element.Elements("Input-Parameters").Count() > 1 || element.Elements("ScripterCode").Count() > 1)
                throw new ArgumentException($"Functoid {id} has repeated parameter or script sections. No partial native map was produced.");
            if (element.Elements("ScripterCode").Elements("Script").Any(script => (string?)script.Attribute("Language") != "CSharp"))
                throw new ArgumentException($"Functoid {id} contains inline XSLT; custom XSLT cannot be imported as a native map, even when unconnected. No partial native map was produced.");
        }
        return result;
    }

    private static int? FunctoidReference(string? text, Dictionary<int, Functoid> functoids)
    {
        if (text is null || text.StartsWith(Sentinel, StringComparison.Ordinal)
            || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        return functoids.ContainsKey(id) ? id : throw new ArgumentException($"A link references unknown functoid {id}. No partial native map was produced.");
    }

    private sealed class FunctoidResolver
    {
        private static readonly Regex ScriptMethod = new(@"\bpublic\s+[\w.<>\[\]]+\s+(\w+)\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private readonly Dictionary<int, Functoid> functoids;
        private readonly XElement[] links;
        private readonly Dictionary<string, XElement> linksById = new(StringComparer.Ordinal);
        private readonly XmlSchemaRoot sourceRoot;
        private readonly Dictionary<int, ResolvedFunctoid> cache = [];
        private readonly HashSet<int> active = [];

        public FunctoidResolver(Dictionary<int, Functoid> functoids, XElement[] links, XmlSchemaRoot sourceRoot)
        {
            this.functoids = functoids;
            this.links = links;
            this.sourceRoot = sourceRoot;
            if (functoids.Count == 0) return;
            foreach (var link in links.Where(link => link.Attribute("LinkID") is not null))
                if (!linksById.TryAdd((string)link.Attribute("LinkID")!, link))
                    throw new ArgumentException($"LinkID '{(string)link.Attribute("LinkID")!}' is declared more than once. No partial native map was produced.");
        }

        public IReadOnlyCollection<int> Contributing => cache.Keys;
        public HashSet<int> Used { get; } = [];

        public ResolvedFunctoid Resolve(int id)
        {
            if (cache.TryGetValue(id, out var known)) return known;
            if (!active.Add(id)) throw new ArgumentException($"Functoid {id} feeds itself through a cycle. No partial native map was produced.");
            try
            {
                var functoid = functoids[id];
                if (!FunctoidCatalog.TryGetValue(functoid.Fid, out var function)) throw Unsupported(functoid);
                var idText = id.ToString(CultureInfo.InvariantCulture);
                var parameters = functoid.Element.Elements("Input-Parameters").Elements("Parameter")
                    .Select(parameter => (Parameter: parameter, Index: int.TryParse((string?)parameter.Attribute("linkIndex"), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : -1))
                    .OrderBy(entry => entry.Index).ToArray();
                if (!parameters.Select(entry => entry.Index).SequenceEqual(Enumerable.Range(0, parameters.Length)))
                    throw new ArgumentException($"Functoid {id} (FID {functoid.Fid}) has missing or duplicate input positions. No partial native map was produced.");
                var arguments = new List<XmlValueExpression>();
                var sources = new List<XmlSchemaField>();
                var nodes = 1;
                var childDepth = 0;
                var linkInputs = 0;
                foreach (var (parameter, _) in parameters)
                {
                    var value = (string?)parameter.Attribute("Value") ?? throw new ArgumentException($"Functoid {id} has an input without a value. No partial native map was produced.");
                    switch ((string?)parameter.Attribute("Type"))
                    {
                        case "constant":
                            arguments.Add(new XmlValueExpression { Kind = XmlValueExpressionKind.Constant, Constant = value });
                            nodes++;
                            childDepth = Math.Max(childDepth, 1);
                            break;
                        case "link":
                            linkInputs++;
                            if (!linksById.TryGetValue(value, out var link) || (string?)link.Attribute("LinkTo") != idText)
                                throw new ArgumentException($"Functoid {id} input refers to link '{value}', which is missing or not wired to this functoid. No partial native map was produced.");
                            var fromText = (string?)link.Attribute("LinkFrom");
                            if (FunctoidReference(fromText, functoids) is { } inner)
                            {
                                var child = Resolve(inner);
                                arguments.Add(child.Expression);
                                sources.AddRange(child.Sources);
                                nodes += child.Nodes;
                                childDepth = Math.Max(childDepth, child.Depth);
                            }
                            else
                            {
                                var field = Find(sourceRoot, fromText);
                                if (!field.Scalar || field.SelfRepeating || field.UnsupportedReason is not null || field.HasDefaultOrFixed)
                                    throw new ArgumentException($"Functoid {id} reads '{field.Path}', which is a record, a repeating value, or otherwise unsupported as a functoid input. No partial native map was produced.");
                                arguments.Add(new XmlValueExpression { Kind = XmlValueExpressionKind.Source, SourcePath = Prefix(field.Path, "s") });
                                sources.Add(field);
                                nodes++;
                                childDepth = Math.Max(childDepth, 1);
                            }
                            break;
                        default:
                            throw new ArgumentException($"Functoid {id} has an input of unsupported type '{(string?)parameter.Attribute("Type")}'. No partial native map was produced.");
                    }
                }
                if (links.Count(link => (string?)link.Attribute("LinkTo") == idText) != linkInputs)
                    throw new ArgumentException($"Functoid {id} has links that its input list does not account for. No partial native map was produced.");
                var (min, max) = XmlMappingFunctionCatalog.Arity(function);
                if (arguments.Count < min || arguments.Count > max)
                    throw new ArgumentException($"Functoid {id} (FID {functoid.Fid}, {function}) has {arguments.Count} input(s); the native function accepts {min}..{max}. No partial native map was produced.");
                if (nodes > XmlMappingFunctionCatalog.MaxNodes || childDepth + 1 > XmlMappingFunctionCatalog.MaxDepth)
                    throw new ArgumentException($"Functoid {id} produces a formula larger than the native limit ({XmlMappingFunctionCatalog.MaxNodes} nodes, depth {XmlMappingFunctionCatalog.MaxDepth}). No partial native map was produced.");
                Used.Add(functoid.Fid);
                var resolved = new ResolvedFunctoid(new XmlValueExpression { Kind = XmlValueExpressionKind.Function, Function = function, Arguments = arguments }, sources, nodes, childDepth + 1);
                cache[id] = resolved;
                return resolved;
            }
            finally
            {
                active.Remove(id);
            }
        }

        private static ArgumentException Unsupported(Functoid functoid)
        {
            if (functoid.Fid != 260)
                return new($"Functoid {functoid.Id} (FID {functoid.Fid}) has no verified native equivalent and requires explicit translation. No partial native map was produced.");
            var code = functoid.Element.Elements("ScripterCode").Elements("Script").Select(script => script.Value).FirstOrDefault() ?? string.Empty;
            var name = ScriptMethod.Match(code) is { Success: true } match ? match.Groups[1].Value : "unnamed";
            return new($"Functoid {functoid.Id} is a scripting functoid with inline C# ('{name}'). Scripts are never auto-translated; translate it explicitly or configure a replacement. No partial native map was produced.");
        }
    }
}
