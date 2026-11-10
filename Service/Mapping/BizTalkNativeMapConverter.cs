using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Mapping;

public sealed record NativeMapConversion(string MappingJson, string Xslt, IReadOnlyList<string> Warnings, IReadOnlyList<string> PendingFunctions);

public static class BizTalkNativeMapConverter
{
    private const string Sentinel = "/*[local-name()='<Schema>']";
    private const string AggregateNamespace = "http://schemas.microsoft.com/BizTalk/2003/aggschema";
    private static readonly Regex Segment = new(@"^(@?)\*\[local-name\(\)='([^']+)'\]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    // BizTalk addresses unnamed sequence groups as <Sequence> path steps; they add no element, so the remaining path must still match exactly one schema field.
    private static readonly Regex SequenceSegment = new(@"^\*\[local-name\(\)='<Sequence>'( and position\(\)='\d+')?\]$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "mapsource", "SrcTree", "TrgTree", "Reference", "TreeValues", "TestValues", "ConstantValues", "Value",
        "Pages", "Page", "Links", "Link", "Functoids", "ScriptTypePrecedence", "CSharp", "ExternalAssembly", "VbNet", "JScript", "XsltCallTemplate", "Xslt",
        "Functoid", "Input-Parameters", "Parameter", "ScripterCode", "Script"
    };

    /// <summary>Functoids with a native equivalent: those flagged by <see cref="XmlMappingFunctionCatalog.HelperVerified"/> match a helper body BizTalk compiled into a real map, the rest follow the documented behavior and are reported as unverified. Anything else stays unconverted and is named in the error.</summary>
    private static readonly IReadOnlyDictionary<int, XmlMappingFunction> FunctoidCatalog = new Dictionary<int, XmlMappingFunction>
    {
        [102] = XmlMappingFunction.StringLeft, [104] = XmlMappingFunction.StringRight, [105] = XmlMappingFunction.StringSize,
        [107] = XmlMappingFunction.StringConcat, [110] = XmlMappingFunction.StringUpperCase, [112] = XmlMappingFunction.MathInt,
        [121] = XmlMappingFunction.MathDivide, [123] = XmlMappingFunction.DateCurrentDate, [125] = XmlMappingFunction.DateCurrentDateTime,
        [311] = XmlMappingFunction.LogicalGt, [315] = XmlMappingFunction.LogicalEq, [316] = XmlMappingFunction.LogicalNe,
        [321] = XmlMappingFunction.LogicalAnd, [701] = XmlMappingFunction.LogicalExistence,
        [103] = XmlMappingFunction.StringLowerCase, [108] = XmlMappingFunction.StringTrimLeft, [109] = XmlMappingFunction.StringTrimRight,
        [111] = XmlMappingFunction.MathAbs, [118] = XmlMappingFunction.MathAdd, [119] = XmlMappingFunction.MathSubtract,
        [120] = XmlMappingFunction.MathMultiply, [312] = XmlMappingFunction.LogicalGte, [313] = XmlMappingFunction.LogicalLt,
        [314] = XmlMappingFunction.LogicalLte, [320] = XmlMappingFunction.LogicalOr, [705] = XmlMappingFunction.LogicalNot,
        [IterationFid] = XmlMappingFunction.RecordPosition, [RecordCountFid] = XmlMappingFunction.RecordCount, [IndexFid] = XmlMappingFunction.IndexValue,
        [324] = XmlMappingFunction.CumulativeSum, [325] = XmlMappingFunction.CumulativeAverage, [326] = XmlMappingFunction.CumulativeMin,
        [327] = XmlMappingFunction.CumulativeMax, [328] = XmlMappingFunction.CumulativeConcat
    };
    private static readonly HashSet<XmlMappingFunction> LogicalFunctions =
    [
        XmlMappingFunction.LogicalGt, XmlMappingFunction.LogicalGte, XmlMappingFunction.LogicalLt, XmlMappingFunction.LogicalLte, XmlMappingFunction.LogicalEq,
        XmlMappingFunction.LogicalNe, XmlMappingFunction.LogicalAnd, XmlMappingFunction.LogicalOr, XmlMappingFunction.LogicalNot, XmlMappingFunction.LogicalExistence
    ];
    private static readonly HashSet<string> MetadataAttributes = new(StringComparer.Ordinal)
    {
        "Name", "Version", "BizTalkServerMapperTool_Version", "XRange", "YRange", "OmitXmlDeclaration", "TreatElementsAsRecords",
        "OptimizeValueMapping", "GenerateDefaultFixedNodes", "PreserveSequenceOrder", "IgnoreNamespacesForLinks", "method", "CopyPIs", "xmlVersion", "UseXSLTransform", "XsltEngine"
    };

    /// <param name="embedSchemas">Persist the XSD documents in the mapping (default). Disable for compact derived choices; the persisted map never validates against them either way, and keeps its source-root assertion.</param>
    public static NativeMapConversion Convert(string mapXml, IReadOnlyList<string> sourceSchemas, IReadOnlyList<string> targetSchemas,
        CancellationToken cancellationToken = default, bool embedSchemas = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var document = XmlMappingSchemas.ReadXml(mapXml);
        var root = document.Root!;
        if (root.Name != "mapsource") throw new ArgumentException("Supply a BizTalk mapsource (.btm) document.");
        var sourceDocuments = WithInlineSchema(root, sourceSchemas);
        var targetDocuments = targetSchemas;
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
        // An empty engine means the map names none (the default); a named engine is an explicit choice that is not proven equivalent.
        CheckSetting("XsltEngine", string.Empty);
        var source = XmlSchemaInspector.Analyze(sourceDocuments, RootName("SrcTree"), cancellationToken);
        var target = XmlSchemaInspector.Analyze(targetDocuments, RootName("TrgTree"), cancellationToken);
        var sourceRoot = SelectRoot(source, "SrcTree");
        var targetRoot = SelectRoot(target, "TrgTree");
        var unsupportedTarget = targetRoot.Fields.FirstOrDefault(field => field.UnsupportedReason is not null && field.Path.EndsWith("/(choice)", StringComparison.Ordinal));
        if (unsupportedTarget is not null)
            throw new ArgumentException($"Target '{unsupportedTarget.Path}' requires explicit translation: {unsupportedTarget.UnsupportedReason}. No partial native map was produced.");
        var namespaces = source.Namespaces.ToDictionary(entry => "s" + entry.Key, entry => entry.Value, StringComparer.Ordinal);
        foreach (var entry in target.Namespaces) namespaces.Add("t" + entry.Key, entry.Value);
        var links = new List<(XmlSchemaField From, XmlSchemaField To)>();
        var copyLinks = new List<(XmlSchemaField From, XmlSchemaField To)>();
        var functoids = ReadFunctoids(root);
        var functoidOutputs = new List<(int Functoid, XmlSchemaField To)>();
        var recordGateLinks = new List<(int Functoid, XmlSchemaField Record)>();
        var resolver = new FunctoidResolver(functoids, root.Descendants("Link").ToArray(), sourceRoot);
        // A target record fed by an XSLT scripting functoid is built by hand-written XSLT that cannot become native fields automatically: it is
        // documented as a stub (together with any Looping inputs wired to the same record) and left for the user to rebuild.
        var stubbedRecords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in root.Descendants("Link"))
            if (FunctoidReference((string?)link.Attribute("LinkFrom"), functoids) is { } scripted && functoids[scripted] is { Fid: ScriptFid, Script.IsXslt: true }
                && FunctoidReference((string?)link.Attribute("LinkTo"), functoids) is null
                && Find(targetRoot, (string?)link.Attribute("LinkTo")) is { Scalar: false } record && record.Path != targetRoot.Path)
                stubbedRecords.Add(record.Path);
        string RecordPath(XmlSchemaField record) => Prefix(record.Path[(targetRoot.Path.Length + 1)..], "t");
        foreach (var link in root.Descendants("Link"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (link.Parent?.Name != "Links") throw new ArgumentException("A map Link is outside a Links collection.");
            var fromText = (string?)link.Attribute("LinkFrom");
            var toText = (string?)link.Attribute("LinkTo");
            var toFunctoid = FunctoidReference(toText, functoids);
            var fromFunctoid = FunctoidReference(fromText, functoids);
            if (toFunctoid is not null) continue; // input wiring is resolved from the functoid's own parameters
            XmlSchemaField from;
            XmlSchemaField to;
            if (fromFunctoid is { } producer)
            {
                if (functoids[producer].Fid == MassCopyFid)
                {
                    var copyFrom = resolver.ResolveMassCopyInput(producer);
                    var copyTo = Find(targetRoot, toText);
                    if (copyFrom.Path == sourceRoot.Path || copyTo.Path == targetRoot.Path)
                        throw new ArgumentException($"Mass Copy functoid {producer} copies to or from a whole message root, which is not supported. No partial native map was produced.");
                    if (AroundStub(copyTo))
                        throw new ArgumentException($"Mass Copy functoid {producer} writes '{copyTo.Path}', which is also built by a scripting functoid. No partial native map was produced.");
                    copyLinks.AddRange(BizTalkMassCopy.Expand(producer, sourceRoot, copyFrom, source.Namespaces, targetRoot, copyTo, target.Namespaces));
                    continue;
                }
                var output = Find(targetRoot, toText);
                if (!output.Scalar && stubbedRecords.Contains(output.Path))
                {
                    if (functoids[producer] is { Fid: ScriptFid, Script.IsXslt: true }) resolver.RegisterRecordStub(producer, RecordPath(output));
                    else resolver.NoteRecordInput(RecordPath(output), producer);
                    continue;
                }
                if (functoids[producer].Fid == LoopingFid && !output.Scalar && output.Path != targetRoot.Path)
                {
                    // A Looping functoid with one record input creates one target record per source record, exactly like a direct record link.
                    from = resolver.ResolveLoopInput(producer);
                    to = output;
                }
                else
                {
                    if (output.Path == targetRoot.Path)
                        throw new ArgumentException($"Functoid {producer} is linked to the target root '{output.Path}'; conditional creation of the whole message is not supported. No partial native map was produced.");
                    if (!output.Scalar)
                    {
                        // A logical functoid linked to a record decides whether the record is created: compiled output wraps the record (inside its loop, if any) in a test on the functoid result.
                        if (!FunctoidCatalog.TryGetValue(functoids[producer].Fid, out var gate) || !LogicalFunctions.Contains(gate))
                            throw new ArgumentException($"Functoid {producer} (FID {functoids[producer].Fid}) is linked to record '{output.Path}'; only a logical functoid (a true/false result) is understood as a condition for creating a record. No partial native map was produced.");
                        recordGateLinks.Add((producer, output));
                        continue;
                    }
                    functoidOutputs.Add((producer, output));
                    continue;
                }
            }
            else
            {
                from = Find(sourceRoot, fromText);
                to = Find(targetRoot, toText);
                if (!to.Scalar && stubbedRecords.Contains(to.Path))
                {
                    resolver.NoteRecordSource(RecordPath(to), $"is also linked directly from {(from.Scalar ? "value" : "record")} {from.Path}");
                    continue;
                }
            }
            if (from.Scalar != to.Scalar)
                throw new ArgumentException($"Link '{from.Path}' -> '{to.Path}' connects a record to a scalar field. No partial native map was produced.");
            if (from.UnsupportedReason is not null || from.HasDefaultOrFixed)
                throw new ArgumentException($"Source '{from.Path}' is unsupported: {from.UnsupportedReason ?? "schema default/fixed values are not supported"}. No partial native map was produced.");
            links.Add((from, to));
        }
        if (copyLinks.Count > 0)
        {
            var written = new HashSet<string>(links.Select(link => link.To.Path).Concat(functoidOutputs.Select(output => output.To.Path)), StringComparer.Ordinal);
            foreach (var constant in root.Descendants("ConstantValues").Elements())
                written.Add(Find(targetRoot, (string?)constant.Attribute("Query")).Path);
            foreach (var (_, copyTarget) in copyLinks)
                if (!written.Add(copyTarget.Path))
                    throw new ArgumentException($"Target '{copyTarget.Path}' is written by a Mass Copy and also by another link, constant, functoid or Mass Copy; BizTalk would emit both. No partial native map was produced.");
            links.AddRange(copyLinks);
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
        void RequireRepeatCoverage(XmlSchemaField from, XmlSchemaField to, bool includeRecord = false, XmlSchemaField? ranging = null)
        {
            foreach (var repeating in sourceRoot.Fields.Where(field => field.SelfRepeating && field.Path != from.Path && field != ranging
                         && from.Path.StartsWith(field.Path + "/", StringComparison.Ordinal)))
                if (!repeatLinks.Any(repeat => repeat.From == repeating && (to.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal) || (includeRecord && to.Path == repeat.To.Path))))
                    throw new ArgumentException($"'{from.Path}' sits below repeating '{repeating.Path}', which has no record link enclosing target '{to.Path}'. No partial native map was produced.");
        }
        foreach (var (from, to) in repeatLinks) RequireRepeatCoverage(from, to);
        // Without a record link, compiled BizTalk output loops over the nearest repeating source record around the nearest repeating target record that the linked fields write to
        // (observed for direct links and functoid inputs alike, and at nested levels). The loop is made explicit here so it can be reviewed and edited as a normal repeat.
        var impliedLoops = new List<(XmlSchemaField From, XmlSchemaField To)>();
        {
            XmlSchemaField? DeepestRepeating(XmlSchemaField source)
                => source is { Scalar: false, SelfRepeating: true }
                    ? source
                    : sourceRoot.Fields.Where(field => field.SelfRepeating && field.Path != source.Path && source.Path.StartsWith(field.Path + "/", StringComparison.Ordinal))
                        .OrderByDescending(field => field.Path.Length).FirstOrDefault();
            XmlSchemaField? NearestTargetRepeat(XmlSchemaField target)
                => targetRoot.Fields.Where(field => !field.Scalar && field.SelfRepeating && field.Path != target.Path && target.Path.StartsWith(field.Path + "/", StringComparison.Ordinal))
                    .OrderByDescending(field => field.Path.Length).FirstOrDefault();
            var driven = links.Where(link => link.From.Scalar && !link.From.SelfRepeating).Select(link => (Sources: (IReadOnlyList<XmlSchemaField>)[link.From], link.To))
                .Concat(functoidOutputs.Select(output => (Sources: resolver.Resolve(output.Functoid).Sources, output.To)))
                .Select(item => (item.Sources, item.To, Repeat: NearestTargetRepeat(item.To)))
                .Where(item => item.Repeat is not null && item.Sources.Count > 0)
                .GroupBy(item => item.Repeat!.Path, StringComparer.Ordinal).OrderBy(group => group.Key.Length);
            foreach (var group in driven)
            {
                var targetRecord = group.First().Repeat!;
                if (repeatLinks.Any(repeat => repeat.To == targetRecord)) continue;
                var anchors = group.SelectMany(item => item.Sources.Select(source => (Anchor: DeepestRepeating(source), item.To)))
                    .Where(pair => pair.Anchor is not null && !repeatLinks.Any(repeat => repeat.From == pair.Anchor && pair.To.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal)))
                    .Select(pair => pair.Anchor!).Distinct().ToList();
                if (anchors.Count == 0) continue;
                var deepest = anchors.OrderByDescending(anchor => anchor.Path.Length).First();
                if (anchors.Any(anchor => anchor != deepest && !deepest.Path.StartsWith(anchor.Path + "/", StringComparison.Ordinal)))
                    throw new ArgumentException($"Fields written below repeating target '{targetRecord.Path}' (which has no record link) read from unrelated repeating source records ({string.Join(", ", anchors.Select(anchor => anchor.Path))}); which loop BizTalk would build is not proven. No partial native map was produced.");
                if (EnclosingRepeat(targetRecord) is { } outer && !deepest.Path.StartsWith(outer.From.Path + "/", StringComparison.Ordinal))
                    throw new ArgumentException($"Fields written below repeating target '{targetRecord.Path}' read repeating source '{deepest.Path}', which is not inside the record link around that target ('{outer.From.Path}'); which loop BizTalk would build is not proven. No partial native map was produced.");
                RequireRepeatCoverage(deepest, targetRecord);
                repeatLinks.Add((deepest, targetRecord));
                impliedLoops.Add((deepest, targetRecord));
            }
        }
        var assignments = new List<(XmlSchemaField Target, XmlFieldMappingDefinition Mapping)>();
        var optionalToRequired = new List<string>();
        var firstOccurrence = new List<string>();
        foreach (var (from, to) in links.Where(link => link.From.Scalar))
        {
            var enclosing = EnclosingRepeat(to);
            // A direct field link with no record link around its target takes the first occurrence: compiled BizTalk output for such links is a plain path with no loop.
            var first = false;
            if (enclosing is null && sourceRoot.Fields.Any(field => field.SelfRepeating && field.Path != from.Path && from.Path.StartsWith(field.Path + "/", StringComparison.Ordinal)))
            {
                firstOccurrence.Add($"{from.Path} -> {to.Path}");
                first = true;
            }
            else RequireRepeatCoverage(from, to);
            var sourceScope = enclosing is { } scoped && from.Path.StartsWith(scoped.From.Path + "/", StringComparison.Ordinal) ? scoped.From.Path : sourceRoot.Path;
            if (enclosing is { } self && from.SelfRepeating && self.From == from) sourceScope = from.Path;
            var sourceRequired = RequiredWithin(sourceRoot, sourceScope, from);
            if (!sourceRequired && RequiredWithin(targetRoot, enclosing?.To.Path ?? targetRoot.Path, to))
                optionalToRequired.Add($"{from.Path} -> {to.Path}");
            assignments.Add((to, new()
            {
                SourcePath = Prefix(from.Path, "s"), TargetPath = TargetPath(to), FirstOccurrence = first,
                MissingValueBehavior = sourceRequired ? MissingXmlValueBehavior.Default : MissingXmlValueBehavior.Omit,
                DefaultValue = sourceRequired ? string.Empty : null
            }));
        }
        foreach (var (producer, to) in functoidOutputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolved = resolver.Resolve(producer);
            resolver.NoteTarget(producer, TargetPath(to));
            var positionRecords = PositionRecordPaths(resolved.Expression).Concat(resolved.Condition is null ? [] : PositionRecordPaths(resolved.Condition)).ToHashSet(StringComparer.Ordinal);
            foreach (var field in resolved.Sources)
            {
                RequireRepeatCoverage(field, to);
                if (!field.Scalar && positionRecords.Contains(Prefix(field.Path, "s")) && !repeatLinks.Any(repeat => repeat.From == field && to.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal)))
                    throw new ArgumentException($"Iteration over repeating record '{field.Path}' feeds '{to.Path}', which is not inside a record link from that record. BizTalk would wrap a loop around just that field, which a native map cannot express. No partial native map was produced.");
            }
            foreach (var scope in resolved.Scopes ?? [])
                if (!repeatLinks.Any(repeat => repeat.From == scope && to.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal)))
                    throw new ArgumentException($"A cumulative functoid scoped to repeating record '{scope.Path}' feeds '{to.Path}', which is not inside a record link from that record. No partial native map was produced.");
            assignments.Add((to, new() { Expression = resolved.Expression, Condition = resolved.Condition, TargetPath = TargetPath(to) }));
        }
        var recordGates = new List<(XmlSchemaField Record, XmlRecordConditionDefinition Definition)>();
        var rangedGates = new List<string>();
        foreach (var (producer, record) in recordGateLinks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (recordGates.Any(gate => gate.Record == record))
                throw new ArgumentException($"Record '{record.Path}' is linked from more than one logical functoid; how BizTalk combines them is not proven. No partial native map was produced.");
            var resolved = resolver.Resolve(producer);
            if (resolved.Condition is not null)
                throw new ArgumentException($"Functoid {producer} linked to record '{record.Path}' is itself conditional; that combination is not supported. No partial native map was produced.");
            resolver.NoteTarget(producer, RecordPath(record));
            // A logical functoid fed by a repeating source record that no record link carries into this record makes BizTalk's compiled output loop over that record around the target and emit the target once per true result.
            // For a record the schema allows once that is "written once when any such record satisfies the condition"; a second match would make BizTalk write a duplicate, so the native map stops instead.
            XmlSchemaField? ranging = null;
            var uncovered = resolved.Sources.Where(field => field.Scalar && !field.SelfRepeating)
                .SelectMany(field => sourceRoot.Fields.Where(repeating => repeating.SelfRepeating && repeating.Path != field.Path && field.Path.StartsWith(repeating.Path + "/", StringComparison.Ordinal)))
                .Where(repeating => !repeatLinks.Any(repeat => repeat.From == repeating && (record.Path == repeat.To.Path || record.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal))))
                .Distinct().ToList();
            if (uncovered.Count == 1 && !record.SelfRepeating && (resolved.Scopes ?? []).Count == 0)
            {
                ranging = uncovered[0];
                // The loop is outside the record in compiled output; a record link from a single (non-repeating) source record is then only an addressing step inside it, so it no longer gates the record.
                var own = repeatLinks.FindIndex(repeat => repeat.To == record && !repeat.From.SelfRepeating);
                if (own >= 0) repeatLinks.RemoveAt(own);
            }
            var positionRecords = PositionRecordPaths(resolved.Expression).ToHashSet(StringComparer.Ordinal);
            foreach (var field in resolved.Sources)
            {
                RequireRepeatCoverage(field, record, includeRecord: true, ranging);
                if (!field.Scalar && positionRecords.Contains(Prefix(field.Path, "s")) && !repeatLinks.Any(repeat => repeat.From == field && (record.Path == repeat.To.Path || record.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal))))
                    throw new ArgumentException($"Iteration over repeating record '{field.Path}' feeds the condition of '{record.Path}', which is not inside a record link from that record. No partial native map was produced.");
            }
            foreach (var scope in resolved.Scopes ?? [])
                if (!repeatLinks.Any(repeat => repeat.From == scope && (record.Path == repeat.To.Path || record.Path.StartsWith(repeat.To.Path + "/", StringComparison.Ordinal))))
                    throw new ArgumentException($"A cumulative functoid scoped to repeating record '{scope.Path}' feeds the condition of '{record.Path}', which is not inside a record link from that record. No partial native map was produced.");
            recordGates.Add((record, new() { TargetPath = RecordPath(record), Condition = resolved.Expression, AnySourcePath = ranging is null ? null : Prefix(ranging.Path, "s") }));
            if (ranging is not null) rangedGates.Add($"{record.Path} (any {ranging.Path})");
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
        bool AroundStub(XmlSchemaField field)
            => stubbedRecords.Any(path => field.Path == path || field.Path.StartsWith(path + "/", StringComparison.Ordinal) || path.StartsWith(field.Path + "/", StringComparison.Ordinal));
        var explicitCount = assignments.Count;
        var fixedPaths = new HashSet<string>(StringComparer.Ordinal);
        // Compiled BizTalk output writes a required target element's fixed schema value even when nothing is linked to it. Other schema default/fixed values are not proven and are refused when they matter.
        foreach (var field in targetRoot.Fields.Where(field => field.HasDefaultOrFixed && field.Path != targetRoot.Path && !AroundStub(field)))
        {
            if (produced.Contains(field.Path))
                throw new ArgumentException($"Target '{field.Path}' has a schema default/fixed value and is also written by a link, functoid or constant; how BizTalk combines them is not proven. No partial native map was produced.");
            var parentPath = field.Path[..field.Path.LastIndexOf('/')];
            if (parentPath != targetRoot.Path && !produced.Contains(parentPath)) continue;
            if (!field.Scalar || field.FixedValue is null || !field.RequiredHere || field.UnsupportedReason is not null)
                throw new ArgumentException($"Target '{field.Path}' requires explicit translation: default values and optional fixed values are not supported (only a required fixed value is written automatically). No partial native map was produced.");
            assignments.Add((field, new() { Constant = field.FixedValue, TargetPath = TargetPath(field) }));
            produced.Add(field.Path);
            fixedPaths.Add(field.Path);
        }
        foreach (var field in targetRoot.Fields.Where(field => field.UnsupportedReason is not null && !AroundStub(field) && produced.Contains(field.Path)))
            throw new ArgumentException($"Target '{field.Path}' requires explicit translation: {field.UnsupportedReason}. No partial native map was produced.");
        bool AlwaysProduces(XmlSchemaField record)
            // Compiled BizTalk output of unrelated partner maps writes an optional record without a record link unconditionally and guards only the fields below it, with any loop for a repeating child inside it (an empty record when nothing loops); the native engine does the same.
            => assignments.Any(assignment => !fixedPaths.Contains(assignment.Target.Path)
                && assignment.Target.Path.StartsWith(record.Path + "/", StringComparison.Ordinal));
        foreach (var (gated, _) in recordGates)
            if (!produced.Contains(gated.Path))
                throw new ArgumentException($"Logical functoid is linked to record '{gated.Path}', which has no mapped fields below it; an empty conditional record is not supported. No partial native map was produced.");
        var alwaysCreated = new List<string>();
        var gatedRequired = new List<string>();
        var omittedRequired = new List<string>();
        foreach (var field in targetRoot.Fields.Where(field => field.Path != targetRoot.Path && !AroundStub(field) && !field.Path.EndsWith("/(optional-group)", StringComparison.Ordinal)))
        {
            var isProduced = produced.Contains(field.Path);
            // Children of an optional record that is not produced are not emitted either, so their own requirement does not apply.
            if (field.Path[..field.Path.LastIndexOf('/')] is var parentPath && parentPath != targetRoot.Path && !produced.Contains(parentPath)) continue;
            if (recordGates.Any(gate => gate.Record == field))
            {
                if (field.RequiredInParent) gatedRequired.Add(field.Path);
                continue;
            }
            if (!isProduced && field.RequiredInParent)
            {
                // Compiled BizTalk output of unrelated partner maps writes nothing for a required target element that has no link, constant or fixed value; the native engine does the same.
                omittedRequired.Add(field.Path);
                continue;
            }
            if (isProduced && !field.Scalar && !field.RequiredInParent && !repeatLinks.Any(repeat => repeat.To == field))
            {
                if (AlwaysProduces(field)) { alwaysCreated.Add(field.Path); continue; }
                throw new ArgumentException($"Optional record '{field.Path}' has no record link and no field written directly below it (it is only reached through record links or across a repeating level); when BizTalk emits it is not proven. No partial native map was produced.");
            }
        }
        foreach (var (_, to) in repeatLinks)
            if (!assignments.Take(explicitCount).Any(assignment => assignment.Target.Path.StartsWith(to.Path + "/", StringComparison.Ordinal) || assignment.Target == to))
                throw new ArgumentException($"Record link to '{to.Path}' has no mapped fields below it; empty linked records are not supported. No partial native map was produced.");
        int Order(XmlSchemaField field) => targetRoot.Fields.TakeWhile(candidate => candidate != field).Count();
        var definition = new XmlMappingDefinition
        {
            Version = 2,
            SourceRoot = Prefix(sourceRoot.Path[1..], "s"),
            TargetRoot = Prefix(targetRoot.Path[1..], "t"), Namespaces = namespaces,
            SourceSchemas = embedSchemas ? sourceDocuments.ToArray() : [], TargetSchemas = embedSchemas ? targetDocuments.ToArray() : [],
            ValidateSource = false, ValidateTarget = false,
            CustomFunctions = resolver.CustomFunctions((string?)root.Attribute("Name") ?? "unnamed map"),
            Fields = assignments.OrderBy(assignment => Order(assignment.Target)).Select(assignment => assignment.Mapping).ToArray(),
            Repeats = repeatLinks.OrderBy(repeat => Order(repeat.To)).Select(repeat => new XmlRepeatMappingDefinition
            {
                SourcePath = Prefix(repeat.From.Path, "s"), TargetPath = Prefix(repeat.To.Path[(targetRoot.Path.Length + 1)..], "t")
            }).ToArray(),
            RecordConditions = recordGates.OrderBy(gate => Order(gate.Record)).Select(gate => gate.Definition).ToArray()
        };
        var json = JsonSerializer.Serialize(definition, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var engine = new XmlMappingEngine(json, allowStubs: true);
        var warnings = new List<string>
        {
            "Review proposal only: scalar values, constants and record/repeat links were converted using the supplied schemas. No deployed policy association, BizTalk-equivalent output, or execution approval is established. Preview synthetic inputs and compare trusted expected outputs before using the map."
        };
        if (recordGates.Count > 0)
            warnings.Add($"{recordGates.Count} target record(s) are written only when a logical functoid linked to the record is true (for example {string.Join("; ", recordGates.Take(3).Select(gate => gate.Record.Path))}); inside a repeating record the condition is evaluated once per repeated record. Compiled BizTalk output of unrelated partner maps wraps the record in such a test; it was not compared with this map's own compiled output.");
        if (gatedRequired.Count > 0)
            warnings.Add($"Conditional record(s) {string.Join("; ", gatedRequired.Take(3))} are required by the target schema, so output is invalid whenever the condition is false.");
        if (rangedGates.Count > 0)
            warnings.Add($"Record(s) {string.Join("; ", rangedGates.Take(3))} are switched on by a functoid fed from a repeating source list: each list entry is tested and the record is written once when one passes. BizTalk would write the record once per passing entry; the native map stops with an error when two or more pass, because the target schema allows the record once.");
        if (fixedPaths.Count > 0)
            warnings.Add($"{fixedPaths.Count} required target element(s) with a fixed schema value were written as constants (for example {string.Join("; ", fixedPaths.Take(3))}). Compiled BizTalk output observed writes the fixed value of a required, unlinked element; it was not compared with this map's own compiled output. Default values and optional fixed values are still refused.");
        var optionalGroups = targetRoot.Fields.Where(field => field.Path.EndsWith("/(optional-group)", StringComparison.Ordinal) && (field.Path.Length == targetRoot.Path.Length + 17 || produced.Contains(field.Path[..field.Path.LastIndexOf('/')])))
            .Select(field => field.Path[..field.Path.LastIndexOf('/')]).Distinct().ToArray();
        if (optionalGroups.Length > 0)
            warnings.Add($"{optionalGroups.Length} target record(s) contain an optional group of elements (for example {string.Join("; ", optionalGroups.Take(3))}). Each member is mapped on its own and whether the whole group is present is not enforced, so output can omit required members of a group that is present. Check the target schema validation results.");
        var repeatingGroups = targetRoot.Fields.Where(field => field.GroupRepeating && !field.Path.Contains("/(") && produced.Contains(field.Path)).Select(field => field.Path).ToArray();
        if (repeatingGroups.Length > 0)
            warnings.Add($"{repeatingGroups.Length} produced target element(s) belong to a repeating group of elements in the schema (for example {string.Join("; ", repeatingGroups.Take(3))}). Each member is mapped as its own repeating record, so all occurrences of one member are written together before the next member. Whether BizTalk interleaves them as repeating sets was not verified against compiled output.");
        if (impliedLoops.Count > 0)
            warnings.Add($"{impliedLoops.Count} record link(s) were inferred because fields below a repeating source record are linked into a repeating target record without a record link (for example {string.Join("; ", impliedLoops.Take(3).Select(loop => $"{loop.From.Path} -> {loop.To.Path}"))}). Compiled BizTalk output of unrelated partner maps loops this way; it was not compared with this map's own compiled output. Review them under Repeating records.");
        if (firstOccurrence.Count > 0)
            warnings.Add($"{firstOccurrence.Count} direct link(s) read a field below a repeating source record with no record link, so only the first occurrence is mapped (for example {string.Join("; ", firstOccurrence.Take(3))}). Compiled BizTalk output observed for such links uses the first occurrence without a loop; it was not compared with this map's own compiled output.");
        if (alwaysCreated.Count > 0)
            warnings.Add($"{alwaysCreated.Count} optional target record(s) with no record link are always created, even when every source below them is absent (for example {string.Join("; ", alwaysCreated.Take(3))}). Compiled BizTalk output of unrelated partner maps does the same: the record is written unconditionally, any loop for a repeating child sits inside it, and only the fields below are guarded; it was not compared with this map's own compiled output. The target schema may still reject an empty record.");
        if (omittedRequired.Count > 0)
            warnings.Add($"{omittedRequired.Count} required target element(s) have no link, constant or fixed value and are not written (for example {string.Join("; ", omittedRequired.Take(3))}). Compiled BizTalk output of unrelated partner maps omits such elements too, so the output can violate the target schema. Link them, or add a constant in the mapping editor, if the receiver needs them.");
        if (optionalToRequired.Count > 0)
            warnings.Add($"{optionalToRequired.Count} link(s) feed a required target from an optional source (for example {string.Join("; ", optionalToRequired.Take(3))}). Like BizTalk, the target element is omitted when the source is absent, so the output can then violate the target schema.");
        if (resolver.Used.Count > 0)
        {
            warnings.Add($"Functoid formulas were converted for {functoidOutputs.Count} target(s) (FID {string.Join(", ", resolver.Used.Order())}). Their behavior ports the helper code BizTalk compiled into other maps; it has not been compared with this map's compiled output. Current-date functions use the Mulse host's local time. Compare trusted expected outputs before use.");
            var unverified = resolver.Used.Where(fid => FunctoidCatalog.TryGetValue(fid, out var used) && !XmlMappingFunctionCatalog.HelperVerified(used)).Order().ToArray();
            if (unverified.Length > 0)
                warnings.Add($"FID {string.Join(", ", unverified)} follow the functoid's documented behavior; no compiled BizTalk helper for them was available to compare. Check their outputs against trusted expected data first.");
        }
        if (resolver.Loopings.Count > 0)
            warnings.Add($"{resolver.Loopings.Count} Looping functoid(s) (FID {LoopingFid}) with a single record input were converted to a plain record link: one target record per source record. Compare trusted expected outputs before use.");
        if (resolver.MassCopies.Count > 0)
            warnings.Add($"{resolver.MassCopies.Count} Mass Copy functoid(s) (FID {MassCopyFid}) were expanded into {copyLinks.Count} explicit record and field link(s) by matching the source and target record structure (identical qualified names, kinds and order). BizTalk copies the source nodes verbatim, so anything outside the declared schemas (undeclared elements, comments, exact whitespace) is not carried over, and optional values follow the normal missing-value rules. Edit the expanded links in the Mapping Designer; compare trusted expected outputs before use.");
        if (resolver.Used.Contains(IterationFid))
            warnings.Add($"Iteration functoid(s) (FID {IterationFid}) became RecordPosition: the 1-based position of the current record among its siblings under the same parent, as BizTalk's compiled position() reports it. Compare trusted expected outputs before use.");
        if (resolver.Used.Contains(RecordCountFid))
            warnings.Add($"Record Count functoid(s) (FID {RecordCountFid}) became RecordCount: the number of nodes at the counted source path (inside a repeating record, within the current record). No compiled BizTalk output was available to confirm this, in particular whether BizTalk counts inside a loop relative to the current record or across the whole message. Compare trusted expected outputs before use.");
        if (resolver.Used.Contains(IndexFid))
            warnings.Add($"Index functoid(s) (FID {IndexFid}) became IndexValue: the value at the index-th occurrence of each repeating ancestor, outermost first, taken from the schema's repeating levels (one index per level). This follows BizTalk's documented behavior; no compiled BizTalk output was available to confirm it, in particular how an index that is missing or out of range behaves (here the target is written empty). Compare trusted expected outputs before use.");
        if (resolver.Used.Overlaps(CumulativeFids))
            warnings.Add($"Cumulative functoid(s) (FID {string.Join(", ", resolver.Used.Where(CumulativeFids.Contains).Order())}) became CumulativeSum/Average/Min/Max/Concat formulas that fold the value over every matching source node (computed or conditional inputs are folded per source record). No compiled BizTalk output was available to confirm the exact behavior, in particular inside a loop (relative to the current record versus across the whole message) or what the optional second (scope) input changes. Compare trusted expected outputs before use.");
        if (resolver.ValueMappings.Count > 0)
            warnings.Add($"Value Mapping functoid(s) (FID {string.Join(", ", resolver.ValueMappings.Order())}) became conditional targets: the target is written only when the condition equals the text 'true' (BizTalk's compiled test), and nothing is written otherwise. FID 374 (flattening) is converted with the same per-record behavior, which is only equivalent while its inputs are single values in scope. A missing value input reads as empty text. Compare trusted expected outputs before use.");
        if (sourceDocuments.Count > sourceSchemas.Count)
            warnings.Add($"This is a multi-input map: its source is BizTalk's aggregate wrapper ({AggregateNamespace}) holding InputMessagePart_0, InputMessagePart_1, ... Each part is one input message. At run time the map input must be built by the xml-parts-render module placed before the mapping (rootName {sourceRoot.Fields[0].LocalPath[1..]}, rootNamespace {AggregateNamespace}, partNames InputMessagePart_0,InputMessagePart_1,... in the map's input order); the part order is NOT recoverable from the map, so check it against the BizTalk orchestration or transform shape that supplied the messages.");
        if (functoids.Count > 0 && resolver.Contributing.Count < functoids.Count)
            warnings.Add($"{functoids.Count - resolver.Contributing.Count} functoid(s) (for example unconnected sample script stubs) are not connected to any target and were ignored, as BizTalk generates no output for them.");
        if (resolver.PendingFunctions.Count > 0)
            warnings.Add($"{resolver.PendingFunctions.Count} scripting functoid(s) were imported as unimplemented stubs ({string.Join(", ", resolver.PendingFunctions.Take(8))}{(resolver.PendingFunctions.Count > 8 ? ", ..." : string.Empty)}). The original C# or XSLT is kept as documentation only and is never run. Until each stub has a formula (or is deleted after its record is rebuilt), this map fails closed and cannot run: fill them in from the custom functions editor (an AI prompt is generated there) or replace the calls.");
        if (resolver.RecordStubTargets.Count > 0)
            warnings.Add($"{resolver.RecordStubTargets.Count} target record(s) are built by hand-written XSLT scripting functoids ({string.Join(", ", resolver.RecordStubTargets.Take(6))}{(resolver.RecordStubTargets.Count > 6 ? ", ..." : string.Empty)}) and were NOT converted: the record and everything below it is missing from the native map, so previews are incomplete and the map must not be enabled. The stub documents the XSLT, its inputs and the Looping inputs; rebuild the record from native field mappings and a record link, then delete the stub.");
        return new(json, engine.Xslt, warnings, resolver.PendingFunctions.ToArray());

        bool RequiredWithin(XmlSchemaRoot tree, string scope, XmlSchemaField field)
            => tree.Fields.Where(candidate => candidate.Path.StartsWith(scope + "/", StringComparison.Ordinal)
                && (candidate.Path == field.Path || field.Path.StartsWith(candidate.Path + "/", StringComparison.Ordinal)))
                .All(candidate => candidate.RequiredInParent);
        void CheckSetting(string name, params string[] expected)
        {
            if (root.Attribute(name) is { } attribute && !expected.Contains(attribute.Value, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException($"Map setting '{name}={attribute.Value}' is not supported by native map import.");
        }
        string? RootName(string name) => root.Elements(name).SingleOrDefault()?.Attribute("RootNode_Name")?.Value is { Length: > 0 } text ? text : null;
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

    // A multi-input map embeds BizTalk's aggregate wrapper schema (Root/InputMessagePart_N, each part referencing an imported schema) inline in
    // SrcTree. It is detached from the map and analyzed together with the part schemas the caller supplies; imports bind by namespace.
    private static List<string> WithInlineSchema(XElement root, IReadOnlyList<string> schemas)
    {
        var result = schemas.ToList();
        XNamespace xsd = "http://www.w3.org/2001/XMLSchema";
        var trees = root.Elements("SrcTree").ToArray();
        if (trees.Length != 1 || trees[0].Elements(xsd + "schema").FirstOrDefault() is not { } inline) return result;
        if (trees[0].Elements().Count() != 1 || trees[0].Elements(xsd + "schema").Count() != 1 || inline.Attribute("targetNamespace")?.Value != AggregateNamespace)
            throw new ArgumentException("The map source contains an inline schema that is not a single BizTalk aggregate wrapper. No partial native map was produced.");
        var text = inline.ToString(SaveOptions.DisableFormatting);
        inline.Remove();
        result.Insert(0, text);
        return result;
    }

    private static XmlSchemaField Find(XmlSchemaRoot root, string? expression)
    {
        if (expression is null || !expression.StartsWith(Sentinel, StringComparison.Ordinal))
            throw new ArgumentException("A link/constant uses an unsupported XPath or functoid reference. No partial native map was produced.");
        var segments = expression[Sentinel.Length..].Split('/', StringSplitOptions.None);
        if (segments.Length < 2 || segments[0].Length != 0)
            throw new ArgumentException("A link must address an explicit schema root and scalar field.");
        var names = segments.Skip(1).Where(part => !SequenceSegment.IsMatch(part)).Select(part =>
        {
            var match = Segment.Match(part);
            if (!match.Success) throw new ArgumentException($"Unsupported BizTalk link segment '{part}'.");
            return match.Groups[1].Value + match.Groups[2].Value;
        });
        var path = "/" + string.Join("/", names);
        var matches = root.Fields.Where(field => field.LocalPath == path).ToArray();
        // A single-schema map can address its message through BizTalk's part wrapper (/Root/InputMessagePart_0/X or /Root/OutputMessagePart_0/X); compiled output of such maps is rooted at X, so the wrapper is dropped, and only when the plain path does not exist.
        if (matches.Length == 0 && PartWrapper.Match(path) is { Success: true } wrapped)
            matches = root.Fields.Where(field => field.LocalPath == wrapped.Groups[1].Value).ToArray();
        return matches.Length == 1 ? matches[0] : throw new ArgumentException($"Schema path '{path}' is missing or namespace-ambiguous; no local-name guess was used.");
    }

    private static readonly Regex PartWrapper = new(@"^/Root/(?:Input|Output)MessagePart_0(/.+)$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static string Prefix(string path, string side)
        => string.Join("/", path.Split('/').Select(segment => segment.Contains(':') && !segment.StartsWith("xml:", StringComparison.Ordinal)
            && !segment.StartsWith("@xml:", StringComparison.Ordinal)
                ? segment.StartsWith('@') ? "@" + side + segment[1..] : side + segment
                : segment));

    private sealed record ScriptChoice(string Language, string Code, IReadOnlyList<(string Language, string Code)> Alternates)
    {
        public bool IsXslt => Language is "Xslt" or "XsltCallTemplate";
    }

    private sealed record Functoid(int Id, int Fid, XElement Element, ScriptChoice? Script = null);

    private sealed record ResolvedFunctoid(XmlValueExpression Expression, IReadOnlyList<XmlSchemaField> Sources, int Nodes, int Depth, XmlValueExpression? Condition = null, IReadOnlyList<XmlSchemaField>? Scopes = null);

    private static IEnumerable<string> PositionRecordPaths(XmlValueExpression expression)
    {
        if (expression.Function == XmlMappingFunction.RecordPosition && expression.Arguments[0].SourcePath is { } path) yield return path;
        foreach (var argument in expression.Arguments ?? [])
            foreach (var inner in PositionRecordPaths(argument)) yield return inner;
    }

    private static readonly string[] DefaultScriptPrecedence = ["CSharp", "ExternalAssembly", "VbNet", "JScript", "XsltCallTemplate", "Xslt"];

    // BizTalk keeps one script per language in a functoid. Unedited default templates are dropped; if several real scripts remain, the map's
    // own script precedence picks the primary one (one compiled example agrees) and the rest are documented as alternates. Nothing is run.
    private static ScriptChoice? ChooseScript(XElement element, IReadOnlyList<string> precedence)
    {
        var scripts = element.Elements("ScripterCode").Elements("Script").Select(script => (Language: (string?)script.Attribute("Language") ?? string.Empty, Code: script.Value)).ToArray();
        if (scripts.Length == 0) return null;
        var real = scripts.Where(script => !string.IsNullOrWhiteSpace(script.Code) && !BizTalkXsltStub.IsSample(script.Code)).ToArray();
        if (real.Length == 0) real = scripts[..1];
        int Rank(string language) => precedence.ToList().IndexOf(language) is var found && found >= 0 ? found : int.MaxValue;
        var ordered = real.Select((script, index) => (script, index)).OrderBy(entry => Rank(entry.script.Language)).ThenBy(entry => entry.index).Select(entry => entry.script).ToArray();
        return new(ordered[0].Language, ordered[0].Code, ordered[1..]);
    }

    private static Dictionary<int, Functoid> ReadFunctoids(XElement root)
    {
        var result = new Dictionary<int, Functoid>();
        var precedence = root.Descendants("ScriptTypePrecedence").Elements().Where(entry => (string?)entry.Attribute("Enabled") != "No").Select(entry => entry.Name.LocalName).ToArray();
        if (precedence.Length == 0) precedence = DefaultScriptPrecedence;
        foreach (var element in root.Descendants("Functoid"))
        {
            if (!int.TryParse((string?)element.Attribute("FunctoidID"), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
                || !int.TryParse((string?)element.Attribute("Functoid-FID"), NumberStyles.None, CultureInfo.InvariantCulture, out var fid))
                throw new ArgumentException("A functoid has a missing or invalid FunctoidID or Functoid-FID. No partial native map was produced.");
            if (element.Elements("Input-Parameters").Count() > 1 || element.Elements("ScripterCode").Count() > 1)
                throw new ArgumentException($"Functoid {id} has repeated parameter or script sections. No partial native map was produced.");
            if (element.Elements("ScripterCode").Elements("Script").FirstOrDefault(script => (string?)script.Attribute("Language") is not ("CSharp" or "Xslt" or "XsltCallTemplate")) is { } unsupported)
                throw new ArgumentException($"Functoid {id} uses script language '{(string?)unsupported.Attribute("Language")}', which cannot be imported (only C# and XSLT scripts become stubs). No partial native map was produced.");
            if (!result.TryAdd(id, new Functoid(id, fid, element, ChooseScript(element, precedence))))
                throw new ArgumentException($"FunctoidID {id} is declared more than once. No partial native map was produced.");
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
        private readonly Dictionary<int, Functoid> functoids;
        private readonly XElement[] links;
        private readonly Dictionary<string, XElement> linksById = new(StringComparer.Ordinal);
        private readonly XmlSchemaRoot sourceRoot;
        private readonly Dictionary<int, ResolvedFunctoid> cache = [];
        private readonly HashSet<int> active = [];
        private readonly Dictionary<int, List<int>> children = [];
        private readonly Dictionary<string, ScriptStub> stubsByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<int, ScriptStub> scriptsByFunctoid = [];
        private readonly Dictionary<int, List<string>> targetsByFunctoid = [];
        private readonly HashSet<int> loopingIds = [];
        private readonly HashSet<int> massCopyIds = [];
        private readonly HashSet<int> otherContributing = [];
        private readonly Dictionary<string, List<string>> recordNotes = new(StringComparer.Ordinal);
        private readonly List<string> recordStubTargets = [];
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

        public IReadOnlyCollection<int> Contributing => cache.Keys.Union(loopingIds).Union(otherContributing).Union(massCopyIds).ToArray();
        public IReadOnlyCollection<int> MassCopies => massCopyIds;
        public IReadOnlyCollection<string> RecordStubTargets => recordStubTargets;
        public IReadOnlyCollection<string> PendingFunctions => stubsByKey.Values.Select(stub => stub.Name).ToArray();
        public HashSet<int> Used { get; } = [];
        public HashSet<int> ValueMappings { get; } = [];
        public IReadOnlyCollection<int> Loopings => loopingIds;

        public XmlSchemaField ResolveMassCopyInput(int id)
        {
            var idText = id.ToString(CultureInfo.InvariantCulture);
            var inputs = links.Where(link => (string?)link.Attribute("LinkTo") == idText).ToArray();
            if (inputs.Length != 1)
                throw new ArgumentException($"Mass Copy functoid {id} must have exactly one source record input but has {inputs.Length}. No partial native map was produced.");
            if (functoids[id].Element.Elements("Input-Parameters").Elements("Parameter").Any(parameter => (string?)parameter.Attribute("Type") != "link"))
                throw new ArgumentException($"Mass Copy functoid {id} has a constant input. No partial native map was produced.");
            var fromText = (string?)inputs[0].Attribute("LinkFrom");
            if (FunctoidReference(fromText, functoids) is not null)
                throw new ArgumentException($"Mass Copy functoid {id} is fed by another functoid; only a direct source record input is supported. No partial native map was produced.");
            var field = Find(sourceRoot, fromText);
            massCopyIds.Add(id);
            return field;
        }

        public XmlSchemaField ResolveLoopInput(int id)
        {
            var idText = id.ToString(CultureInfo.InvariantCulture);
            var inputs = links.Where(link => (string?)link.Attribute("LinkTo") == idText).ToArray();
            if (inputs.Length == 0)
                throw new ArgumentException($"Looping functoid {id} has no input record. No partial native map was produced.");
            if (inputs.Length > 1)
                throw new ArgumentException($"Looping functoid {id} merges {inputs.Length} source records into one target record, which a native map cannot express yet (it needs one loop per input). No partial native map was produced.");
            if (functoids[id].Element.Elements("Input-Parameters").Elements("Parameter").Any(parameter => (string?)parameter.Attribute("Type") != "link"))
                throw new ArgumentException($"Looping functoid {id} has a constant input. No partial native map was produced.");
            var fromText = (string?)inputs[0].Attribute("LinkFrom");
            if (FunctoidReference(fromText, functoids) is not null)
                throw new ArgumentException($"Looping functoid {id} is fed by another functoid; only a direct source record input is supported. No partial native map was produced.");
            var field = Find(sourceRoot, fromText);
            if (field.Scalar)
                throw new ArgumentException($"Looping functoid {id} is fed by scalar '{field.Path}'; it loops over records. No partial native map was produced.");
            loopingIds.Add(id);
            return field;
        }

        public ResolvedFunctoid Resolve(int id)
        {
            if (cache.TryGetValue(id, out var known)) return known;
            if (!active.Add(id)) throw new ArgumentException($"Functoid {id} feeds itself through a cycle. No partial native map was produced.");
            try
            {
                var functoid = functoids[id];
                var isScript = functoid.Fid == ScriptFid;
                var isValueMapping = functoid.Fid is ValueMappingFid or ValueMappingFlatteningFid;
                XmlMappingFunction function = default;
                if (!isScript && !isValueMapping && !FunctoidCatalog.TryGetValue(functoid.Fid, out function)) throw Unsupported(functoid);
                var inputNotes = new List<string>();
                var idText = id.ToString(CultureInfo.InvariantCulture);
                var parameters = functoid.Element.Elements("Input-Parameters").Elements("Parameter")
                    .Select(parameter => (Parameter: parameter, Index: int.TryParse((string?)parameter.Attribute("linkIndex"), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : -1))
                    .OrderBy(entry => entry.Index).ToArray();
                if (!parameters.Select(entry => entry.Index).SequenceEqual(Enumerable.Range(0, parameters.Length)))
                    throw new ArgumentException($"Functoid {id} (FID {functoid.Fid}) has missing or duplicate input positions. No partial native map was produced.");
                if (CumulativeFids.Contains(functoid.Fid)) return ResolveCumulative(id, functoid, function, parameters.Select(entry => entry.Parameter).ToArray());
                if (functoid.Fid == IndexFid) return ResolveIndex(id, functoid, function, parameters.Select(entry => entry.Parameter).ToArray());
                var arguments = new List<XmlValueExpression>();
                var sources = new List<XmlSchemaField>();
                var scopeFields = new List<XmlSchemaField>();
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
                            inputNotes.Add($"constant \"{BizTalkScriptStub.Clip(value, 60)}\"");
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
                                if (child.Condition is not null)
                                    throw new ArgumentException($"Value Mapping functoid {inner} feeds functoid {id}. A Value Mapping produces no value when its condition is false, which a formula cannot express; only a direct link from a Value Mapping to a target field is supported. No partial native map was produced.");
                                (children.TryGetValue(id, out var childIds) ? childIds : children[id] = []).Add(inner);
                                inputNotes.Add($"result of functoid {inner} (FID {functoids[inner].Fid})");
                                arguments.Add(child.Expression);
                                sources.AddRange(child.Sources);
                                scopeFields.AddRange(child.Scopes ?? []);
                                nodes += child.Nodes;
                                childDepth = Math.Max(childDepth, child.Depth);
                            }
                            else
                            {
                                var field = Find(sourceRoot, fromText);
                                if (functoid.Fid switch
                                    {
                                        IterationFid => field.Scalar || !field.SelfRepeating || field.UnsupportedReason is not null || field.HasDefaultOrFixed,
                                        RecordCountFid => field.UnsupportedReason is not null,
                                        _ => !field.Scalar || field.SelfRepeating || field.UnsupportedReason is not null || field.HasDefaultOrFixed
                                    })
                                    throw new ArgumentException(functoid.Fid switch
                                    {
                                        IterationFid => $"Iteration functoid {id} must read a repeating source record, but '{field.Path}' is not one. No partial native map was produced.",
                                        RecordCountFid => $"Record Count functoid {id} reads '{field.Path}', which is unsupported ({field.UnsupportedReason}). No partial native map was produced.",
                                        _ => $"Functoid {id} reads '{field.Path}', which is a record, a repeating value, or otherwise unsupported as a functoid input. No partial native map was produced."
                                    });
                                arguments.Add(new XmlValueExpression { Kind = XmlValueExpressionKind.Source, SourcePath = Prefix(field.Path, "s") });
                                inputNotes.Add($"source {field.Path}");
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
                if (functoid.Fid == IterationFid && (arguments.Count != 1 || arguments[0].Kind != XmlValueExpressionKind.Source))
                    throw new ArgumentException($"Iteration functoid {id} needs exactly one source record input. No partial native map was produced.");
                if (functoid.Fid == RecordCountFid && (arguments.Count != 1 || arguments[0].Kind != XmlValueExpressionKind.Source))
                    throw new ArgumentException($"Record Count functoid {id} needs exactly one input read directly from the source message (not a constant or another functoid). No partial native map was produced.");
                if (isScript)
                {
                    if (nodes > XmlMappingFunctionCatalog.MaxNodes || childDepth + 1 > XmlMappingFunctionCatalog.MaxDepth)
                        throw new ArgumentException($"Functoid {id} produces a formula larger than the native limit ({XmlMappingFunctionCatalog.MaxNodes} nodes, depth {XmlMappingFunctionCatalog.MaxDepth}). No partial native map was produced.");
                    var call = new XmlValueExpression { Kind = XmlValueExpressionKind.Custom, Name = RegisterScript(functoid, inputNotes), Arguments = arguments };
                    var stub = new ResolvedFunctoid(call, sources, nodes, childDepth + 1, Scopes: scopeFields);
                    cache[id] = stub;
                    return stub;
                }
                if (isValueMapping)
                {
                    if (arguments.Count != 2)
                        throw new ArgumentException($"Value Mapping functoid {id} (FID {functoid.Fid}) has {arguments.Count} input(s); it needs a condition and a value. No partial native map was produced.");
                    if (nodes > XmlMappingFunctionCatalog.MaxNodes || childDepth + 1 > XmlMappingFunctionCatalog.MaxDepth)
                        throw new ArgumentException($"Functoid {id} produces a formula larger than the native limit ({XmlMappingFunctionCatalog.MaxNodes} nodes, depth {XmlMappingFunctionCatalog.MaxDepth}). No partial native map was produced.");
                    ValueMappings.Add(functoid.Fid);
                    var conditional = new ResolvedFunctoid(arguments[1], sources, nodes, childDepth + 1, arguments[0], scopeFields);
                    cache[id] = conditional;
                    return conditional;
                }
                var (min, max) = XmlMappingFunctionCatalog.Arity(function);
                if (arguments.Count < min || arguments.Count > max)
                    throw new ArgumentException($"Functoid {id} (FID {functoid.Fid}, {function}) has {arguments.Count} input(s); the native function accepts {min}..{max}. No partial native map was produced.");
                if (nodes > XmlMappingFunctionCatalog.MaxNodes || childDepth + 1 > XmlMappingFunctionCatalog.MaxDepth)
                    throw new ArgumentException($"Functoid {id} produces a formula larger than the native limit ({XmlMappingFunctionCatalog.MaxNodes} nodes, depth {XmlMappingFunctionCatalog.MaxDepth}). No partial native map was produced.");
                Used.Add(functoid.Fid);
                var resolved = new ResolvedFunctoid(new XmlValueExpression { Kind = XmlValueExpressionKind.Function, Function = function, Arguments = arguments }, sources, nodes, childDepth + 1, Scopes: scopeFields);
                cache[id] = resolved;
                return resolved;
            }
            finally
            {
                active.Remove(id);
            }
        }

        private ResolvedFunctoid ResolveCumulative(int id, Functoid functoid, XmlMappingFunction function, XElement[] parameters)
        {
            var idText = id.ToString(CultureInfo.InvariantCulture);
            if (parameters.Length is < 1 or > 2)
                throw new ArgumentException($"Cumulative functoid {id} (FID {functoid.Fid}) has {parameters.Length} input(s); it needs a value and optionally a scope. No partial native map was produced.");
            if (parameters.Any(parameter => (string?)parameter.Attribute("Type") != "link"))
                throw new ArgumentException($"Cumulative functoid {id} (FID {functoid.Fid}) has a constant input. No partial native map was produced.");
            var inputs = parameters.Select(parameter =>
            {
                var value = (string?)parameter.Attribute("Value") ?? throw new ArgumentException($"Functoid {id} has an input without a value. No partial native map was produced.");
                return linksById.TryGetValue(value, out var link) && (string?)link.Attribute("LinkTo") == idText
                    ? (string?)link.Attribute("LinkFrom")
                    : throw new ArgumentException($"Functoid {id} input refers to link '{value}', which is missing or not wired to this functoid. No partial native map was produced.");
            }).ToArray();
            if (links.Count(link => (string?)link.Attribute("LinkTo") == idText) != inputs.Length)
                throw new ArgumentException($"Functoid {id} has links that its input list does not account for. No partial native map was produced.");
            XmlSchemaField? scope = null;
            if (inputs.Length == 2)
            {
                if (FunctoidReference(inputs[1], functoids) is not null)
                    throw new ArgumentException($"Cumulative functoid {id} takes its scope from another functoid; only a source record read directly from the message is supported. No partial native map was produced.");
                scope = Find(sourceRoot, inputs[1]);
            }
            var scopes = new List<XmlSchemaField>();
            List<XmlValueExpression> arguments;
            var nodes = 2;
            var depth = 1;
            if (FunctoidReference(inputs[0], functoids) is { } inner)
            {
                if (scope is not null)
                    throw new ArgumentException($"Cumulative functoid {id} has a scope input and a computed value; that combination is not supported yet. No partial native map was produced.");
                var child = Resolve(inner);
                (children.TryGetValue(id, out var childIds) ? childIds : children[id] = []).Add(inner);
                var anchors = child.Sources
                    .Select(source => sourceRoot.Fields.Where(field => field.SelfRepeating && field.Path != source.Path && source.Path.StartsWith(field.Path + "/", StringComparison.Ordinal))
                        .OrderByDescending(field => field.Path.Length).FirstOrDefault())
                    .Where(field => field is not null).Select(field => field!.Path).Distinct(StringComparer.Ordinal).ToArray();
                if (anchors.Length > 1)
                    throw new ArgumentException($"Cumulative functoid {id} folds values from different repeating records ({string.Join(", ", anchors)}); a native fold needs one record to loop over. No partial native map was produced.");
                arguments = [new() { Kind = XmlValueExpressionKind.Source, SourcePath = Prefix(anchors.Length == 1 ? anchors[0] : sourceRoot.Path, "s") }];
                if (child.Condition is not null) arguments.Add(child.Condition);
                arguments.Add(child.Expression);
                nodes += child.Nodes;
                depth = child.Depth + 1;
                scopes.AddRange(child.Scopes ?? []);
            }
            else
            {
                var field = Find(sourceRoot, inputs[0]);
                if (!field.Scalar || field.UnsupportedReason is not null || field.HasDefaultOrFixed)
                    throw new ArgumentException($"Cumulative functoid {id} reads '{field.Path}', which is a record or otherwise unsupported as a value input. No partial native map was produced.");
                if (scope is not null)
                {
                    if (field.Path != scope.Path && !field.Path.StartsWith(scope.Path + "/", StringComparison.Ordinal))
                        throw new ArgumentException($"Cumulative functoid {id} has scope '{scope.Path}', which does not contain its value '{field.Path}'. No partial native map was produced.");
                    if (scope.SelfRepeating) scopes.Add(scope);
                }
                arguments = [new() { Kind = XmlValueExpressionKind.Source, SourcePath = Prefix(field.Path, "s") }];
            }
            if (nodes > XmlMappingFunctionCatalog.MaxNodes || depth + 1 > XmlMappingFunctionCatalog.MaxDepth)
                throw new ArgumentException($"Functoid {id} produces a formula larger than the native limit ({XmlMappingFunctionCatalog.MaxNodes} nodes, depth {XmlMappingFunctionCatalog.MaxDepth}). No partial native map was produced.");
            Used.Add(functoid.Fid);
            var resolved = new ResolvedFunctoid(new XmlValueExpression { Kind = XmlValueExpressionKind.Function, Function = function, Arguments = arguments }, [], nodes, depth + 1, Scopes: scopes);
            cache[id] = resolved;
            return resolved;
        }

        // Index(value, i1, i2, ...) reads the value at one occurrence of each repeating ancestor, outermost first. The schema decides which ancestors repeat,
        // so the converter lists them as explicit levels; an index count that does not match the number of repeating ancestors is refused rather than guessed.
        private ResolvedFunctoid ResolveIndex(int id, Functoid functoid, XmlMappingFunction function, XElement[] parameters)
        {
            var idText = id.ToString(CultureInfo.InvariantCulture);
            if (parameters.Length < 2)
                throw new ArgumentException($"Index functoid {id} has {parameters.Length} input(s); it needs a value and at least one index. No partial native map was produced.");
            var linkCount = 0;
            var inputs = parameters.Select(parameter =>
            {
                var value = (string?)parameter.Attribute("Value") ?? throw new ArgumentException($"Functoid {id} has an input without a value. No partial native map was produced.");
                switch ((string?)parameter.Attribute("Type"))
                {
                    case "constant": return (Constant: value, From: (string?)null);
                    case "link":
                        linkCount++;
                        return linksById.TryGetValue(value, out var link) && (string?)link.Attribute("LinkTo") == idText
                            ? (Constant: null, From: (string?)link.Attribute("LinkFrom"))
                            : throw new ArgumentException($"Functoid {id} input refers to link '{value}', which is missing or not wired to this functoid. No partial native map was produced.");
                    default: throw new ArgumentException($"Functoid {id} has an input of unsupported type '{(string?)parameter.Attribute("Type")}'. No partial native map was produced.");
                }
            }).ToArray();
            if (links.Count(link => (string?)link.Attribute("LinkTo") == idText) != linkCount)
                throw new ArgumentException($"Functoid {id} has links that its input list does not account for. No partial native map was produced.");
            if (inputs[0].From is null || FunctoidReference(inputs[0].From, functoids) is not null)
                throw new ArgumentException($"Index functoid {id} must read its value directly from the source message (not a constant or another functoid). No partial native map was produced.");
            var leaf = Find(sourceRoot, inputs[0].From);
            if (!leaf.Scalar || leaf.UnsupportedReason is not null || leaf.HasDefaultOrFixed)
                throw new ArgumentException($"Index functoid {id} reads '{leaf.Path}', which is a record or otherwise unsupported as a value input. No partial native map was produced.");
            var levels = sourceRoot.Fields.Where(field => field.SelfRepeating && (field.Path == leaf.Path || leaf.Path.StartsWith(field.Path + "/", StringComparison.Ordinal)))
                .OrderBy(field => field.Path.Length).ToArray();
            if (levels.Length != inputs.Length - 1)
                throw new ArgumentException($"Index functoid {id} has {inputs.Length - 1} index input(s), but '{leaf.Path}' sits in {levels.Length} repeating level(s){(levels.Length == 0 ? string.Empty : " (" + string.Join(", ", levels.Select(level => level.LocalPath)) + ")")}; the mapping from indexes to levels is only converted when the counts match. No partial native map was produced.");
            var arguments = new List<XmlValueExpression> { new() { Kind = XmlValueExpressionKind.Source, SourcePath = Prefix(leaf.Path, "s") } };
            var sources = new List<XmlSchemaField>();
            var scopes = new List<XmlSchemaField>();
            var nodes = 2;
            var depth = 1;
            for (var position = 0; position < levels.Length; position++)
            {
                arguments.Add(new() { Kind = XmlValueExpressionKind.Source, SourcePath = Prefix(levels[position].Path, "s") });
                nodes += 2;
                var input = inputs[position + 1];
                if (input.From is null)
                {
                    arguments.Add(new() { Kind = XmlValueExpressionKind.Constant, Constant = input.Constant });
                    continue;
                }
                if (FunctoidReference(input.From, functoids) is { } inner)
                {
                    var child = Resolve(inner);
                    if (child.Condition is not null)
                        throw new ArgumentException($"Value Mapping functoid {inner} feeds Index functoid {id}; a Value Mapping produces no value when its condition is false, which a formula cannot express. No partial native map was produced.");
                    (children.TryGetValue(id, out var childIds) ? childIds : children[id] = []).Add(inner);
                    arguments.Add(child.Expression);
                    sources.AddRange(child.Sources);
                    scopes.AddRange(child.Scopes ?? []);
                    nodes += child.Nodes;
                    depth = Math.Max(depth, child.Depth + 1);
                    continue;
                }
                var index = Find(sourceRoot, input.From);
                if (!index.Scalar || index.SelfRepeating || index.UnsupportedReason is not null || index.HasDefaultOrFixed)
                    throw new ArgumentException($"Index functoid {id} takes an index from '{index.Path}', which is a record, a repeating value, or otherwise unsupported. No partial native map was produced.");
                arguments.Add(new() { Kind = XmlValueExpressionKind.Source, SourcePath = Prefix(index.Path, "s") });
                sources.Add(index);
                nodes++;
            }
            if (nodes > XmlMappingFunctionCatalog.MaxNodes || depth + 1 > XmlMappingFunctionCatalog.MaxDepth)
                throw new ArgumentException($"Functoid {id} produces a formula larger than the native limit ({XmlMappingFunctionCatalog.MaxNodes} nodes, depth {XmlMappingFunctionCatalog.MaxDepth}). No partial native map was produced.");
            Used.Add(functoid.Fid);
            var resolved = new ResolvedFunctoid(new XmlValueExpression { Kind = XmlValueExpressionKind.Function, Function = function, Arguments = arguments }, sources, nodes, depth + 1, Scopes: scopes);
            cache[id] = resolved;
            return resolved;
        }

        private static ArgumentException Unsupported(Functoid functoid)
            => new($"Functoid {functoid.Id} (FID {functoid.Fid}) has no verified native equivalent and requires explicit translation. No partial native map was produced.");

        public void NoteTarget(int producer, string targetPath)
        {
            var seen = new HashSet<int>();
            var pending = new Stack<int>([producer]);
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (!seen.Add(current)) continue;
                if (scriptsByFunctoid.ContainsKey(current))
                {
                    var list = targetsByFunctoid.TryGetValue(current, out var known) ? known : targetsByFunctoid[current] = [];
                    if (!list.Contains(targetPath)) list.Add(targetPath);
                }
                if (children.TryGetValue(current, out var inner)) foreach (var child in inner) pending.Push(child);
            }
        }

		private static string Sanitize(string text) => new(text.Select(character => char.IsAsciiLetterOrDigit(character) ? character : '_').ToArray());

		private void MarkContributing(int id)
		{
			var pending = new Stack<int>([id]);
			while (pending.Count > 0)
			{
				var current = pending.Pop();
				if (!otherContributing.Add(current)) continue;
				var currentText = current.ToString(CultureInfo.InvariantCulture);
				foreach (var link in links.Where(link => (string?)link.Attribute("LinkTo") == currentText))
					if (FunctoidReference((string?)link.Attribute("LinkFrom"), functoids) is { } upstream) pending.Push(upstream);
			}
		}

		// Describes a functoid's inputs for documentation only; unlike Resolve it accepts record inputs and does not build an expression.
		private IReadOnlyList<string> DescribeInputs(int id)
		{
			var idText = id.ToString(CultureInfo.InvariantCulture);
			var parameters = functoids[id].Element.Elements("Input-Parameters").Elements("Parameter")
				.Select(parameter => (Parameter: parameter, Index: int.TryParse((string?)parameter.Attribute("linkIndex"), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ? index : -1))
				.OrderBy(entry => entry.Index).ToArray();
			if (!parameters.Select(entry => entry.Index).SequenceEqual(Enumerable.Range(0, parameters.Length)))
				throw new ArgumentException($"Functoid {id} (FID {functoids[id].Fid}) has missing or duplicate input positions. No partial native map was produced.");
			var notes = new List<string>();
			var linkInputs = 0;
			foreach (var (parameter, _) in parameters)
			{
				var value = (string?)parameter.Attribute("Value") ?? throw new ArgumentException($"Functoid {id} has an input without a value. No partial native map was produced.");
				switch ((string?)parameter.Attribute("Type"))
				{
					case "constant":
						notes.Add($"constant \"{BizTalkScriptStub.Clip(value, 60)}\"");
						break;
					case "link":
						linkInputs++;
						if (!linksById.TryGetValue(value, out var link) || (string?)link.Attribute("LinkTo") != idText)
							throw new ArgumentException($"Functoid {id} input refers to link '{value}', which is missing or not wired to this functoid. No partial native map was produced.");
						var fromText = (string?)link.Attribute("LinkFrom");
						if (FunctoidReference(fromText, functoids) is { } inner)
						{
							notes.Add($"result of functoid {inner} (FID {functoids[inner].Fid})");
							MarkContributing(inner);
						}
						else
						{
							var field = Find(sourceRoot, fromText);
							notes.Add($"{(field.Scalar ? "source" : "source record")} {field.Path}{(field.SelfRepeating ? " (repeating)" : string.Empty)}");
						}
						break;
					default:
						throw new ArgumentException($"Functoid {id} has an input of unsupported type '{(string?)parameter.Attribute("Type")}'. No partial native map was produced.");
				}
			}
			if (links.Count(link => (string?)link.Attribute("LinkTo") == idText) != linkInputs)
				throw new ArgumentException($"Functoid {id} has links that its input list does not account for. No partial native map was produced.");
			return notes;
		}

		/// <summary>Documents a target record that hand-written XSLT builds. Nothing is converted for it; the stub only carries the description.</summary>
		public void RegisterRecordStub(int producer, string recordPath)
		{
			RegisterScript(functoids[producer], DescribeInputs(producer), recordPath);
			NoteTarget(producer, recordPath);
			otherContributing.Add(producer);
			if (!recordStubTargets.Contains(recordPath)) recordStubTargets.Add(recordPath);
		}

		public void NoteRecordInput(string recordPath, int functoid)
		{
			var inputs = string.Join("; ", DescribeInputs(functoid));
			otherContributing.Add(functoid);
			NoteRecordSource(recordPath, functoids[functoid].Fid == LoopingFid
				? $"Looping functoid {functoid} creates one record for each node of its inputs: {inputs}"
				: $"functoid {functoid} (FID {functoids[functoid].Fid}) is also linked to it (inputs: {inputs})");
		}

		public void NoteRecordSource(string recordPath, string note)
		{
			if (!recordNotes.TryGetValue(recordPath, out var notes)) recordNotes[recordPath] = notes = [];
			if (!notes.Contains(note)) notes.Add(note);
		}

		private string RegisterScript(Functoid functoid, IReadOnlyList<string> inputNotes, string? recordPath = null)
		{
			var script = functoid.Script;
			if (script is null || string.IsNullOrWhiteSpace(script.Code))
				throw new ArgumentException($"Functoid {functoid.Id} is a scripting functoid without any code. No partial native map was produced.");
			var code = script.Code;
			var signature = script.IsXslt ? null : BizTalkScriptStub.Parse(BizTalkScriptStub.Strip(code));
			var baseName = script.IsXslt
				? script.Language == "XsltCallTemplate" && BizTalkXsltStub.Template(code) is { } template ? "Template_" + Sanitize(template) : null
				: signature?.Name;
			if (baseName is null || !XmlMappingFunctionCatalog.IsIdentifier(baseName) || baseName.Length > 56 || Enum.TryParse<XmlMappingFunction>(baseName, out _))
				baseName = $"{(script.IsXslt ? "Xslt" : "Script")}_{functoid.Id.ToString(CultureInfo.InvariantCulture)}";
			var key = $"{baseName}\0{inputNotes.Count}\0{recordPath is not null}\0{script.Language}\0{code}";
			if (!stubsByKey.TryGetValue(key, out var stub))
			{
				var name = baseName;
				for (var suffix = 2; stubsByKey.Values.Any(existing => existing.Name == name); suffix++) name = $"{baseName}_{suffix}";
				var declared = script.IsXslt ? BizTalkXsltStub.Parameters(code).ToArray() : signature?.Parameters?.Select(parameter => parameter.Name).ToArray();
				var names = declared is not null && declared.Length == inputNotes.Count && declared.All(XmlMappingFunctionCatalog.IsIdentifier) && declared.Distinct(StringComparer.Ordinal).Count() == declared.Length
					? declared
					: Enumerable.Range(1, inputNotes.Count).Select(index => $"param{index}").ToArray();
				stub = new ScriptStub(name, script, signature, names, recordPath is not null);
				stubsByKey[key] = stub;
			}
			stub.Uses.Add((functoid.Id, inputNotes.ToArray()));
			scriptsByFunctoid[functoid.Id] = stub;
			return stub.Name;
		}

		public IReadOnlyList<XmlCustomFunctionDefinition> CustomFunctions(string mapName)
		{
			if (stubsByKey.Count > XmlMappingFunctionCatalog.MaxCustomFunctions)
				throw new ArgumentException($"The map has {stubsByKey.Count} distinct scripting functoids; at most {XmlMappingFunctionCatalog.MaxCustomFunctions} can be imported as stubs. No partial native map was produced.");
			return stubsByKey.Values.Select(stub =>
			{
				var script = stub.Script;
				var stripped = script.IsXslt ? script.Code : BizTalkScriptStub.Strip(script.Code);
				var text = new System.Text.StringBuilder();
				if (script.IsXslt)
				{
					text.AppendLine($"Imported BizTalk scripting functoid (FID {ScriptFid}, {script.Language}) from map '{mapName}'. The original XSLT is kept below for reference and is never run.");
					text.AppendLine(stub.IsRecord
						? "This functoid builds a whole target record, not one value, so it cannot be written as a formula and nothing was converted for it. Rebuild the record from native field mappings plus a record link in the mapping designer, then delete this stub. The map fails closed while the stub exists."
						: "This functoid writes its output into one target field. Implement the function as a formula that returns the text value the XSLT computes (the field mapping writes the element itself), or replace the call.");
					if (BizTalkXsltStub.Template(script.Code) is { } templateName) text.AppendLine($"Named template: {templateName}");
				}
				else
				{
					text.AppendLine($"Imported BizTalk scripting functoid (FID {ScriptFid}) from map '{mapName}'. The original C# is kept below for reference and is never run: implement this function as a formula (Body) to use the map.");
					if (stub.Signature is { } signature)
					{
						text.AppendLine($"Original signature: {signature.Display}");
						text.AppendLine($"Returns: {signature.ReturnType}");
						if (signature.PublicMethods > 1) text.AppendLine($"Note: the script declares {signature.PublicMethods} public methods; the first was described.");
					}
					else text.AppendLine("The method signature could not be read from the script.");
				}
				var first = stub.Uses[0];
				for (var index = 0; index < stub.ParameterNames.Count; index++)
					text.AppendLine($"Input {index + 1} ({stub.ParameterNames[index]}{(stub.Signature?.Parameters is { } declared && declared.Count == stub.ParameterNames.Count ? ": " + declared[index].Type : string.Empty)}) - first use: {first.Inputs[index]}");
				var hints = script.IsXslt ? BizTalkXsltStub.Describe(script.Language, stripped) : BizTalkScriptStub.Hints(stripped);
				if (hints.Count > 0) text.AppendLine("Detected behavior: " + string.Join("; ", hints) + ".");
				foreach (var path in stub.Uses.SelectMany(use => targetsByFunctoid.TryGetValue(use.FunctoidId, out var fed) ? (IEnumerable<string>)fed : Array.Empty<string>()).Distinct(StringComparer.Ordinal))
					if (recordNotes.TryGetValue(path, out var notes))
						foreach (var note in notes) text.AppendLine($"Target record {path}: {note}.");
				if (script.Alternates.Count > 0)
					text.AppendLine($"Note: this functoid also holds {string.Join(", ", script.Alternates.Select(alternate => alternate.Language))} script text. BizTalk keeps one script per language and the map file does not name the active one; the {script.Language} script was chosen from the map's script precedence (one compiled example agrees). The others are appended to the original source below.");
				var usages = stub.Uses.Take(XmlMappingFunctionCatalog.MaxUsages).Select(use =>
					BizTalkScriptStub.Clip($"Map '{mapName}', functoid {use.FunctoidId}: inputs ({string.Join("; ", use.Inputs)})"
						+ (targetsByFunctoid.TryGetValue(use.FunctoidId, out var targets) && targets.Count > 0 ? " -> feeds " + string.Join(", ", targets) : string.Empty),
						XmlMappingFunctionCatalog.MaxUsageLength)).ToArray();
				var original = script.Code.Trim();
				foreach (var alternate in script.Alternates)
					original += $"\n\n----- alternate {alternate.Language} script (not chosen) -----\n{alternate.Code.Trim()}";
				return new XmlCustomFunctionDefinition
				{
					Name = stub.Name,
					Description = BizTalkScriptStub.Clip(text.ToString().TrimEnd(), XmlMappingFunctionCatalog.MaxDescriptionLength),
					Parameters = stub.ParameterNames.Select((name, index) => new XmlCustomFunctionParameter
					{
						Name = name,
						Description = BizTalkScriptStub.Clip(stub.Signature?.Parameters is { } declared && declared.Count == stub.ParameterNames.Count ? declared[index].Type : "unknown type", XmlMappingFunctionCatalog.MaxDescriptionLength)
					}).ToArray(),
					OriginalSource = BizTalkScriptStub.Clip(original, XmlMappingFunctionCatalog.MaxOriginalSourceLength),
					OriginalSourceLanguage = script.Language,
					Usages = usages
				};
			}).ToArray();
		}
    }

    private const int ScriptFid = 260;
    private const int LoopingFid = 424;
    private const int MassCopyFid = 802;
    private const int IterationFid = 474;
    private const int RecordCountFid = 322;
    private const int IndexFid = 323;
    private static readonly HashSet<int> CumulativeFids = [324, 325, 326, 327, 328];
    private const int ValueMappingFlatteningFid = 374;
    private const int ValueMappingFid = 375;

    private sealed class ScriptStub(string name, ScriptChoice script, BizTalkScriptSignature? signature, IReadOnlyList<string> parameterNames, bool isRecord)
    {
        public string Name { get; } = name;
        public ScriptChoice Script { get; } = script;
        public bool IsRecord { get; } = isRecord;
        public BizTalkScriptSignature? Signature { get; } = signature;
        public IReadOnlyList<string> ParameterNames { get; } = parameterNames;
        public List<(int FunctoidId, string[] Inputs)> Uses { get; } = [];
    }
}
