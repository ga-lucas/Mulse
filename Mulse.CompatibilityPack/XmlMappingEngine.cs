using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Mulse.Modules.Mapping;

namespace Mulse.CompatibilityPack;

public sealed partial class XmlMappingEngine
{
    public const int MaxSampleCharacters = 1_000_000;
    private static readonly XNamespace Xsl = "http://www.w3.org/1999/XSL/Transform";
    private const string FunctionPrefix = "mulse-fn";
    private readonly XslCompiledTransform _transform = new();
    private readonly System.Xml.Schema.XmlSchemaSet? _sourceSchemas;
    private readonly System.Xml.Schema.XmlSchemaSet? _targetSchemas;
    private readonly TimeProvider _timeProvider;
    public string Xslt { get; }

    /// <param name="timeProvider">Clock for the current-date functions (defaults to the system clock, local time like BizTalk).</param>
    /// <param name="allowStubs">Only for review/preview of an import: calls of unimplemented custom functions compile to a call that throws when run. The default rejects such maps.</param>
    public XmlMappingEngine(string mappingJson, TimeProvider? timeProvider = null, bool allowStubs = false)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (string.IsNullOrWhiteSpace(mappingJson) || mappingJson.Length > 1_048_576)
            throw new ArgumentException("XML mapping JSON is required and must not exceed 1 MiB characters.");
        XmlMappingDefinition map;
        try
        {
            map = JsonSerializer.Deserialize<XmlMappingDefinition>(mappingJson, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
            }) ?? throw new ArgumentException("XML mapping JSON must be an object.");
        }
        catch (JsonException exception) { throw new ArgumentException($"Invalid XML mapping JSON: {exception.Message}", exception); }
        if (map.SourceSchemas is null || map.TargetSchemas is null
            || (map.Version == 1 && (map.SourceSchemas.Count > 0 || map.TargetSchemas.Count > 0 || map.SourceRoot is not null)))
            throw new ArgumentException("Source-root assertions and persisted XSD validation require version 2 and non-null schema collections.");
        if (map.SourceSchemas.Count > 0) _sourceSchemas = XmlMappingSchemas.Compile(map.SourceSchemas);
        if (map.TargetSchemas.Count > 0) _targetSchemas = XmlMappingSchemas.Compile(map.TargetSchemas);
        if (!map.ValidateSource) _sourceSchemas = null;
        if (!map.ValidateTarget) _targetSchemas = null;
        Xslt = Generate(map, allowStubs);
        using var reader = XmlReader.Create(new StringReader(Xslt), ReaderSettings());
        _transform.Load(reader, XsltSettings.Default, stylesheetResolver: null);
    }

    public string Transform(string xml, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(xml) || xml.Length > MaxSampleCharacters)
            throw new ArgumentException("XML input is required and must not exceed 1,000,000 characters.");
        try
        {
            if (_sourceSchemas is not null) XmlMappingSchemas.Validate(xml, _sourceSchemas, "Source", cancellationToken);
            using var reader = XmlReader.Create(new StringReader(xml), ReaderSettings());
            using var output = new BoundedWriter(cancellationToken);
            var arguments = new XsltArgumentList();
            arguments.AddExtensionObject(XmlMappingFunctions.Namespace, new XmlMappingFunctions(_timeProvider));
            using (var writer = XmlWriter.Create(output, _transform.OutputSettings))
                _transform.Transform(reader, arguments, writer);
            var result = output.ToString();
            if (_targetSchemas is not null) XmlMappingSchemas.Validate(result, _targetSchemas, "Target", cancellationToken);
            return result;
        }
        catch (XmlException exception) { throw new ArgumentException($"Invalid XML input: {exception.Message}", exception); }
        catch (XsltException exception) { throw new ArgumentException($"XML mapping failed: {exception.Message}", exception); }
    }

    private static XmlReaderSettings ReaderSettings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = MaxSampleCharacters
    };

    private static string Generate(XmlMappingDefinition map, bool allowStubs)
    {
        if (map.Version is not (1 or 2))
            throw new ArgumentException($"XML mapping version {map.Version} is not supported.");
        if (map.Namespaces is null || map.Namespaces.Count > 32 || map.Fields is null || map.Fields.Count is < 1 or > 512)
            throw new ArgumentException("XML mappings require 1 to 512 fields and at most 32 namespace prefixes.");
        var stylesheet = new XElement(Xsl + "stylesheet", new XAttribute("version", "1.0"), new XAttribute(XNamespace.Xmlns + "xsl", Xsl));
        foreach (var (prefix, uri) in map.Namespaces)
        {
            VerifyName(prefix);
            if (prefix.StartsWith("xml", StringComparison.OrdinalIgnoreCase) || prefix == "xsl" || prefix == FunctionPrefix || prefix == NodeSetPrefix || string.IsNullOrWhiteSpace(uri) || uri == XmlMappingFunctions.Namespace
                || uri == NodeSetNamespace
                || !Uri.TryCreate(uri, UriKind.Absolute, out _) || uri == Xsl.NamespaceName || uri == XNamespace.Xml.NamespaceName || uri == XNamespace.Xmlns.NamespaceName)
                throw new ArgumentException($"Invalid or reserved XML namespace prefix/URI '{prefix}'. Use explicit, non-reserved prefixes and absolute URIs.");
            stylesheet.Add(new XAttribute(XNamespace.Xmlns + prefix, uri));
        }
        var custom = ReadCustomFunctions(map, allowStubs);
        if (map.Fields.Any(field => field is { Expression: not null }) || map.RecordConditions is { Count: > 0 })
        {
            stylesheet.Add(new XAttribute(XNamespace.Xmlns + FunctionPrefix, XmlMappingFunctions.Namespace), new XAttribute("exclude-result-prefixes", FunctionPrefix));
        }
        if (map.Repeats is null || map.Repeats.Count > 256 || (map.Version == 1 && map.Repeats.Count > 0))
            throw new ArgumentException("Repeating records require version 2 and at most 256 repeats.");
        if (map.RecordConditions is null || map.RecordConditions.Count > 256 || (map.Version != 2 && map.RecordConditions.Count > 0))
            throw new ArgumentException("Record conditions require version 2 and at most 256 conditions.");
        var repeatTargets = new List<(XmlRepeatMappingDefinition Repeat, IReadOnlyList<(XName Name, bool IsAttribute)> Segments)>();
        foreach (var repeat in map.Repeats)
        {
            if (repeat is null) throw new ArgumentException("XML mapping repeats cannot be null.");
            var targetSegments = ParsePath(repeat.TargetPath, absolute: false, map);
            var sourceSegments = ParsePath(repeat.SourcePath, absolute: true, map);
            if (targetSegments[^1].IsAttribute || sourceSegments[^1].IsAttribute)
                throw new ArgumentException($"Repeat '{repeat.SourcePath}' -> '{repeat.TargetPath}' must select elements, not attributes.");
            if (repeatTargets.Any(existing => existing.Repeat.TargetPath == repeat.TargetPath))
                throw new ArgumentException($"Target '{repeat.TargetPath}' has more than one repeat.");
            repeatTargets.Add((repeat, targetSegments));
        }
        foreach (var (repeat, segments) in repeatTargets)
        {
            var parent = EnclosingRepeat(repeatTargets, segments, proper: true);
            if (parent is not null && !repeat.SourcePath.StartsWith(parent.SourcePath + "/", StringComparison.Ordinal))
                throw new ArgumentException($"Nested repeat '{repeat.TargetPath}' must iterate a source below its enclosing repeat '{parent.TargetPath}'.");
        }
        var target = new TargetNode(ResolveName(map.TargetRoot, map));
        var template = new XElement(Xsl + "template", new XAttribute("match", "/"));
        if (map.SourceRoot is not null)
        {
            ResolveName(map.SourceRoot, map);
            template.Add(new XElement(Xsl + "if", new XAttribute("test", $"count(/{map.SourceRoot}) != 1"),
                new XElement(Xsl + "message", new XAttribute("terminate", "yes"), $"Input must have source root '{map.SourceRoot}'.")));
        }
        foreach (var field in map.Fields)
        {
            if (field is null) throw new ArgumentException("XML mapping fields cannot be null.");
            if (field.Operations is null || field.Operations.Count > 16 || (map.Version == 1 && field.Operations.Count > 0)
                || field.Operations.Any(operation => operation is null || !Enum.IsDefined(operation.Kind)
                    || (operation.Kind is XmlStringOperationKind.Prefix or XmlStringOperationKind.Suffix
                        ? operation.Argument is null || operation.Argument.Length > 4096 : operation.Argument is not null)))
                throw new ArgumentException($"Mapping '{field.TargetPath}' has invalid string operations. Up to 16 operations require version 2; only Prefix/Suffix take a literal argument (at most 4,096 characters).");
            var segments = ParsePath(field.TargetPath, absolute: false, map);
            if (field.Constant is not null && !string.IsNullOrEmpty(field.SourcePath))
                throw new ArgumentException($"Mapping '{field.TargetPath}' must specify either a source path or a constant, not both.");
            if (!Enum.IsDefined(field.MissingValueBehavior)
                || (map.Version == 1 && (field.MissingValueBehavior != MissingXmlValueBehavior.Error || field.DefaultValue is not null))
                || (field.Constant is not null && (field.MissingValueBehavior != MissingXmlValueBehavior.Error || field.DefaultValue is not null))
                || (field.MissingValueBehavior == MissingXmlValueBehavior.Default ? field.DefaultValue is null : field.DefaultValue is not null))
                throw new ArgumentException($"Mapping '{field.TargetPath}' has an invalid missing-value policy. Omit/default requires version 2, a source path, and a default value only for the Default policy.");
            var sourcePaths = new List<string>();
            var effective = field;
            if (field.FirstOccurrence && (map.Version != 2 || field.Expression is not null || field.Constant is not null || string.IsNullOrEmpty(field.SourcePath)))
                throw new ArgumentException($"Mapping '{field.TargetPath}' uses first-occurrence selection, which requires version 2 and a source path (no expression or constant).");
            if (field.Condition is not null && (map.Version != 2 || field.Expression is null))
                throw new ArgumentException($"Mapping '{field.TargetPath}' uses a condition, which requires version 2 and an expression value.");
            if (field.Expression is not null)
            {
                if (map.Version != 2 || !string.IsNullOrEmpty(field.SourcePath) || field.Constant is not null
                    || field.MissingValueBehavior != MissingXmlValueBehavior.Error || field.DefaultValue is not null)
                    throw new ArgumentException($"Mapping '{field.TargetPath}' uses an expression, which requires version 2 and no source path, constant or missing-value policy.");
                var nodes = 0;
                ValidateExpression(field.Expression, field.TargetPath, 1, ref nodes, sourcePaths, custom, null);
                var expanded = 0;
                XmlValueExpression? condition = null;
                if (field.Condition is not null)
                {
                    var conditionNodes = 0;
                    ValidateExpression(field.Condition, field.TargetPath, 1, ref conditionNodes, sourcePaths, custom, null);
                    condition = Expand(field.Condition, null, custom, [], ref expanded, 1, allowStubs, field.TargetPath);
                }
                effective = field with { Expression = Expand(field.Expression, null, custom, [], ref expanded, 1, allowStubs, field.TargetPath), Condition = condition };
                RequireRecordPositionSources(effective.Expression!, map.Repeats, field.TargetPath);
                if (condition is not null) RequireRecordPositionSources(condition, map.Repeats, field.TargetPath);
            }
            else if (field.Constant is null) sourcePaths.Add(field.SourcePath);
            RequireEnclosingRepeats(map, repeatTargets, segments, field.TargetPath, sourcePaths,
                effective.Expression is null ? [] : ExemptPaths(effective.Expression).Concat(effective.Condition is null ? [] : ExemptPaths(effective.Condition)));
            var current = target;
            foreach (var segment in segments)
            {
                if (current.Assignment is not null)
                    throw new ArgumentException($"Target '{field.TargetPath}' overlaps a scalar target.");
                var child = current.Children.FirstOrDefault(node => node.Name == segment.Name && node.IsAttribute == segment.IsAttribute);
                if (child is null)
                {
                    child = new TargetNode(segment.Name, segment.IsAttribute);
                    current.Children.Add(child);
                }
                current = child;
            }
            if (current.Assignment is not null || current.Children.Count > 0)
                throw new ArgumentException($"Target '{field.TargetPath}' is duplicated or overlaps another target.");
            current.Assignment = effective;
        }
        foreach (var (repeat, segments) in repeatTargets)
        {
            var node = target;
            foreach (var segment in segments)
                node = node.Children.FirstOrDefault(child => child.Name == segment.Name && child.IsAttribute == segment.IsAttribute)
                    ?? throw new ArgumentException($"Repeat target '{repeat.TargetPath}' has no mapped fields below it. Empty repeated records are not supported.");
            node.Repeat = repeat;
        }
        var conditionedRecords = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recordCondition in map.RecordConditions)
        {
            if (recordCondition?.Condition is null) throw new ArgumentException("Record conditions need a target path and a condition.");
            var segments = ParsePath(recordCondition.TargetPath, absolute: false, map);
            if (!conditionedRecords.Add(recordCondition.TargetPath))
                throw new ArgumentException($"Record '{recordCondition.TargetPath}' has more than one condition.");
            var node = target;
            foreach (var segment in segments)
                node = node.Children.FirstOrDefault(child => child.Name == segment.Name && child.IsAttribute == segment.IsAttribute)
                    ?? throw new ArgumentException($"Record condition target '{recordCondition.TargetPath}' has no mapped fields below it. A conditional record needs at least one mapped field.");
            if (node.IsAttribute || node.Assignment is not null)
                throw new ArgumentException($"Record condition target '{recordCondition.TargetPath}' is a field, not a record. Use a field condition instead.");
            var sourcePaths = new List<string>();
            var conditionNodes = 0;
            ValidateExpression(recordCondition.Condition, recordCondition.TargetPath, 1, ref conditionNodes, sourcePaths, custom, null);
            var expanded = 0;
            var expandedCondition = Expand(recordCondition.Condition, null, custom, [], ref expanded, 1, allowStubs, recordCondition.TargetPath);
            RequireRecordPositionSources(expandedCondition, map.Repeats, recordCondition.TargetPath);
            RequireEnclosingRepeats(map, repeatTargets, segments, recordCondition.TargetPath, sourcePaths, ExemptPaths(expandedCondition));
            node.RecordCondition = expandedCondition;
        }
        var folds = new FoldContext();
        var rootChecks = new List<XElement>();
        var body = Emit(target, [], rootChecks, folds);
        template.Add(rootChecks);
        template.Add(body);
        if (folds.UsesNodeSet)
        {
            stylesheet.Add(new XAttribute(XNamespace.Xmlns + NodeSetPrefix, NodeSetNamespace));
            stylesheet.SetAttributeValue("exclude-result-prefixes", $"{FunctionPrefix} {NodeSetPrefix}");
        }
        stylesheet.Add(new XElement(Xsl + "output", new XAttribute("method", "xml"), new XAttribute("omit-xml-declaration", "yes"), new XAttribute("indent", "yes")), template);
        return stylesheet.ToString();
    }

    // Folds over computed values (cumulative functoids) are hoisted into a variable holding one <v> per record, because XPath 1.0 cannot map a formula over a node-set.
    private const string NodeSetPrefix = "msxsl";
    private const string NodeSetNamespace = "urn:schemas-microsoft-com:xslt";

    private sealed class FoldContext
    {
        public int Count { get; set; }
        public bool UsesNodeSet { get; set; }
    }

    private static bool IsFold(XmlMappingFunction function)
        => function is XmlMappingFunction.CumulativeSum or XmlMappingFunction.CumulativeAverage or XmlMappingFunction.CumulativeMin
            or XmlMappingFunction.CumulativeMax or XmlMappingFunction.CumulativeConcat;

    // Arguments are passed as XPath strings (like BizTalk's string(...) wrappers); results of logical functions become the text true/false.
    // Variables a fold needs are appended to prelude (null where none can be declared, e.g. a condition).
    private static string Compile(XmlValueExpression expression, IReadOnlyList<string> scopes, FoldContext folds, List<XElement>? prelude)
    {
        switch (expression.Kind)
        {
            case XmlValueExpressionKind.Source: return SourceExpression(expression.SourcePath!, scopes);
            case XmlValueExpressionKind.Constant: return Literal(expression.Constant!);
            case XmlValueExpressionKind.Custom: return $"{FunctionPrefix}:Unimplemented({Literal(expression.Name!)})";
        }
        string Text(XmlValueExpression argument) => argument.Kind == XmlValueExpressionKind.Constant ? Compile(argument, scopes, folds, prelude) : $"string({Compile(argument, scopes, folds, prelude)})";
        var function = expression.Function!.Value;
        if (IsFold(function)) return CompileFold(function, expression, scopes, folds, prelude);
        if (function == XmlMappingFunction.IndexValue) return CompileIndex(expression, scopes, folds, prelude);
        if (function == XmlMappingFunction.RecordPosition)
        {
            var scope = scopes.ToList().LastIndexOf(expression.Arguments[0].SourcePath!);
            if (scope < 0) throw new ArgumentException($"RecordPosition source '{expression.Arguments[0].SourcePath}' is not an enclosing repeat of its target.");
            return $"string($p{scope + 1})";
        }
        if (function == XmlMappingFunction.RecordCount)
        {
            var counted = expression.Arguments[0].SourcePath!;
            if (scopes.Contains(counted)) throw new ArgumentException($"RecordCount source '{counted}' is the repeating record the target sits in; count a different path.");
            return $"string(count({SourceExpression(counted, scopes)}))";
        }
        var arguments = expression.Arguments.Select(Text).ToArray();
        switch (function)
        {
            case XmlMappingFunction.StringConcat: return $"concat({string.Join(", ", arguments)})";
            case XmlMappingFunction.LogicalExistence: return $"{FunctionPrefix}:LogicalExistence(boolean({Compile(expression.Arguments[0], scopes, folds, prelude)}))";
            case XmlMappingFunction.LogicalAnd:
            case XmlMappingFunction.LogicalOr:
            case XmlMappingFunction.MathAdd:
            case XmlMappingFunction.MathSubtract:
            case XmlMappingFunction.MathMultiply:
                var result = arguments[0];
                for (var index = 1; index < arguments.Length; index++)
                    result = $"{FunctionPrefix}:{function}(string({result}), {arguments[index]})";
                return result;
            default: return $"{FunctionPrefix}:{function}({string.Join(", ", arguments)})";
        }
    }

    // IndexValue(value, level1, index1, ...) selects the index-th occurrence of each listed ancestor of the value as one XPath: .../level1[position() = i1]/.../value.
    // A formula index is evaluated once into a variable, because inside a predicate a relative path would refer to the candidate node, not the target's record.
    private static string CompileIndex(XmlValueExpression expression, IReadOnlyList<string> scopes, FoldContext folds, List<XElement>? prelude)
    {
        var arguments = expression.Arguments;
        var leaf = arguments[0].SourcePath!;
        if (scopes.Contains(leaf)) throw new ArgumentException($"IndexValue source '{leaf}' is the repeating record the target sits in.");
        var anchorIndex = scopes.ToList().FindLastIndex(scope => leaf.StartsWith(scope + "/", StringComparison.Ordinal));
        var anchor = anchorIndex < 0 ? string.Empty : scopes[anchorIndex];
        var prefix = anchorIndex < 0 || anchorIndex == scopes.Count - 1 ? string.Empty : $"$r{anchorIndex + 1}";
        var predicates = new Dictionary<string, string>(StringComparer.Ordinal);
        var previous = anchor;
        for (var position = 1; position < arguments.Count; position += 2)
        {
            var level = arguments[position].SourcePath!;
            if ((level != leaf && !leaf.StartsWith(level + "/", StringComparison.Ordinal)) || !level.StartsWith(previous + "/", StringComparison.Ordinal))
                throw new ArgumentException($"IndexValue level '{level}' must be an ancestor of '{leaf}' (or the value itself), below the repeat the target sits in, and listed outermost first.");
            previous = level;
            var index = arguments[position + 1];
            if (index.Kind == XmlValueExpressionKind.Constant) predicates[level] = $"[position() = number({Compile(index, scopes, folds, prelude)})]";
            else
            {
                if (prelude is null) throw new ArgumentException("IndexValue with a computed index cannot be used in a condition; compute it in the target field instead.");
                var name = $"ix{++folds.Count}";
                prelude.Add(new XElement(Xsl + "variable", new XAttribute("name", name), new XAttribute("select", $"number({Compile(index, scopes, folds, prelude)})")));
                predicates[level] = $"[position() = ${name}]";
            }
        }
        var path = new StringBuilder(prefix);
        var current = string.Empty;
        foreach (var segment in leaf.Split('/').Skip(1))
        {
            current += "/" + segment;
            if (current.Length <= anchor.Length) continue;
            if (path.Length > 0 || anchor.Length == 0) path.Append('/');
            path.Append(segment).Append(predicates.GetValueOrDefault(current));
        }
        return path.ToString();
    }

    private static string CompileFold(XmlMappingFunction function, XmlValueExpression expression, IReadOnlyList<string> scopes, FoldContext folds, List<XElement>? prelude)
    {
        var anchor = expression.Arguments[0].SourcePath!;
        if (scopes.Contains(anchor)) throw new ArgumentException($"{function} source '{anchor}' is the repeating record the target sits in; fold a different path.");
        if (expression.Arguments.Count == 1) return $"{FunctionPrefix}:{function}({SourceExpression(anchor, scopes)})";
        if (prelude is null) throw new ArgumentException($"{function} over a record and a value cannot be used in a condition; compute it in the target field instead.");
        var inner = scopes.Append(anchor).ToArray();
        var level = inner.Length;
        var loop = new XElement(Xsl + "for-each", new XAttribute("select", SourceExpression(anchor, scopes)));
        loop.Add(new XElement(Xsl + "variable", new XAttribute("name", $"r{level}"), new XAttribute("select", ".")));
        loop.Add(new XElement(Xsl + "variable", new XAttribute("name", $"p{level}"), new XAttribute("select", "position()")));
        var declarations = new List<XElement>();
        var value = Compile(expression.Arguments[^1], inner, folds, declarations);
        var test = expression.Arguments.Count == 3 ? $"string({Compile(expression.Arguments[1], inner, folds, declarations)}) = 'true'" : null;
        loop.Add(declarations);
        var item = new XElement("v", new XElement(Xsl + "value-of", new XAttribute("select", value)));
        loop.Add(test is null ? item : new XElement(Xsl + "if", new XAttribute("test", test), item));
        var name = $"fold{++folds.Count}";
        prelude.Add(new XElement(Xsl + "variable", new XAttribute("name", name), loop));
        folds.UsesNodeSet = true;
        return $"{FunctionPrefix}:{function}({NodeSetPrefix}:node-set(${name})/v)";
    }

    // Paths a count or fold only selects (never reads as one value per target record), so they may lie below a repeat the target is not in.
    private static IEnumerable<string> ExemptPaths(XmlValueExpression expression)
    {
        if (expression.Function == XmlMappingFunction.RecordCount && expression.Arguments.Count == 1 && expression.Arguments[0].Kind == XmlValueExpressionKind.Source)
            yield return expression.Arguments[0].SourcePath!;
        else if (expression.Function == XmlMappingFunction.IndexValue)
        {
            // The value and its levels are selected by position; the index formulas are ordinary single-value reads.
            for (var position = 0; position < expression.Arguments.Count; position += position == 0 ? 1 : 2)
                if (expression.Arguments[position].Kind == XmlValueExpressionKind.Source) yield return expression.Arguments[position].SourcePath!;
            for (var position = 2; position < expression.Arguments.Count; position += 2)
                foreach (var path in ExemptPaths(expression.Arguments[position])) yield return path;
            yield break;
        }
        else if (expression.Function is { } function && IsFold(function))
        {
            foreach (var path in XmlMappingFunctionCatalog.SourcePaths(expression)) yield return path;
            yield break;
        }
        foreach (var argument in expression.Arguments)
            foreach (var path in ExemptPaths(argument)) yield return path;
    }

    // A source path inside a repeating record may only be read from a target at or below that repeat's target. A counted path is only counted, never read as a value, so counting a repeating record from outside it is allowed.
    private static void RequireEnclosingRepeats(XmlMappingDefinition map, IReadOnlyList<(XmlRepeatMappingDefinition Repeat, IReadOnlyList<(XName Name, bool IsAttribute)> Segments)> repeatTargets,
        IReadOnlyList<(XName Name, bool IsAttribute)> segments, string targetPath, IReadOnlyList<string> sourcePaths, IEnumerable<string> counted)
    {
        var enclosing = new HashSet<XmlRepeatMappingDefinition>(repeatTargets
            .Where(item => item.Segments.Count <= segments.Count && item.Segments.Zip(segments).All(pair => pair.First == pair.Second))
            .Select(item => item.Repeat));
        var scoped = sourcePaths.ToList();
        foreach (var path in counted) scoped.Remove(path);
        foreach (var sourcePath in sourcePaths)
        {
            ParsePath(sourcePath, absolute: true, map);
            if (!scoped.Contains(sourcePath)) continue;
            foreach (var repeat in map.Repeats)
                if ((sourcePath == repeat.SourcePath || sourcePath.StartsWith(repeat.SourcePath + "/", StringComparison.Ordinal)) && !enclosing.Contains(repeat))
                    throw new ArgumentException($"Mapping '{targetPath}' reads '{sourcePath}' inside repeat '{repeat.SourcePath}' but is not below the repeat's target '{repeat.TargetPath}'.");
        }
    }

    private static void RequireRecordPositionSources(XmlValueExpression expression, IReadOnlyList<XmlRepeatMappingDefinition> repeats, string target)
    {
        if (expression.Function == XmlMappingFunction.RecordPosition
            && (expression.Arguments[0].Kind != XmlValueExpressionKind.Source || !repeats.Any(repeat => repeat.SourcePath == expression.Arguments[0].SourcePath)))
            throw new ArgumentException($"RecordPosition in '{target}' must be given the source path of a repeating record (a repeat's source path).");
        if (expression.Function == XmlMappingFunction.RecordCount && expression.Arguments[0].Kind != XmlValueExpressionKind.Source)
            throw new ArgumentException($"RecordCount in '{target}' must be given a source path.");
        if (expression.Function is { } fold && IsFold(fold) && expression.Arguments[0].Kind != XmlValueExpressionKind.Source)
            throw new ArgumentException($"{fold} in '{target}' must start with a source path (the nodes to fold).");
        if (expression.Function == XmlMappingFunction.IndexValue
            && (expression.Arguments.Count % 2 == 0 || expression.Arguments[0].Kind != XmlValueExpressionKind.Source
                || expression.Arguments.Where((_, position) => position % 2 == 1).Any(level => level.Kind != XmlValueExpressionKind.Source)))
            throw new ArgumentException($"IndexValue in '{target}' must be a source path followed by pairs of a source path (the repeating record) and an index formula.");
        foreach (var argument in expression.Arguments) RequireRecordPositionSources(argument, repeats, target);
    }

    private static string Literal(string text)
    {
        if (!text.Contains('\'')) return $"'{text}'";
        if (!text.Contains('"')) return $"\"{text}\"";
        return "concat(" + string.Join(", \"'\", ", text.Split('\'').Select(part => $"'{part}'")) + ")";
    }

    private static XmlRepeatMappingDefinition? EnclosingRepeat(
        IReadOnlyList<(XmlRepeatMappingDefinition Repeat, IReadOnlyList<(XName Name, bool IsAttribute)> Segments)> repeats,
        IReadOnlyList<(XName Name, bool IsAttribute)> segments, bool proper)
        => repeats
            .Where(item => item.Segments.Count < segments.Count || (!proper && item.Segments.Count == segments.Count))
            .Where(item => item.Segments.Zip(segments).All(pair => pair.First == pair.Second))
            .OrderByDescending(item => item.Segments.Count)
            .Select(item => item.Repeat)
            .FirstOrDefault();

    private static string SourceExpression(string path, IReadOnlyList<string> scopes)
    {
        for (var index = scopes.Count - 1; index >= 0; index--)
        {
            var prefix = index == scopes.Count - 1 ? string.Empty : $"$r{index + 1}";
            if (path == scopes[index]) return prefix.Length == 0 ? "." : prefix;
            if (path.StartsWith(scopes[index] + "/", StringComparison.Ordinal))
                return prefix.Length == 0 ? path[(scopes[index].Length + 1)..] : prefix + "/" + path[(scopes[index].Length + 1)..];
        }
        return path;
    }

    private const string NilTest = "[@*[local-name()='nil' and namespace-uri()='http://www.w3.org/2001/XMLSchema-instance' and (.='true' or .='1')]]";

    private static XElement Emit(TargetNode node, IReadOnlyList<string> scopes, List<XElement> checks, FoldContext folds)
    {
        var inner = node.Repeat is null ? scopes : [.. scopes, node.Repeat.SourcePath];
        var scopeChecks = node.Repeat is null && node.RecordCondition is null ? checks : [];
        var element = new XElement(Xsl + (node.IsAttribute ? "attribute" : "element"),
            new XAttribute("name", node.Name.LocalName), new XAttribute("namespace", node.Name.NamespaceName));
        string? source = null;
        if (node.Assignment is { } field)
        {
            if (field.Constant is null && field.Expression is null)
            {
                source = SourceExpression(field.SourcePath, inner);
                var first = source == "." ? "." : $"({source})[1]";
                var cardinality = field.MissingValueBehavior == MissingXmlValueBehavior.Error ? "!= 1" : "> 1";
                var cardinalityTest = field.FirstOccurrence
                    ? (field.MissingValueBehavior == MissingXmlValueBehavior.Error ? $"count({source}) = 0 or {first}/*" : $"{first}/*")
                    : $"count({source}) {cardinality} or {source}/*";
                scopeChecks.Add(new XElement(Xsl + "if", new XAttribute("test", $"{cardinalityTest} or {(source == "." ? "self::node()" : field.FirstOccurrence ? first : source)}{NilTest}"),
                    new XElement(Xsl + "message", new XAttribute("terminate", "yes"), field.MissingValueBehavior == MissingXmlValueBehavior.Error
                        ? $"Source '{field.SourcePath}' must select exactly one scalar element or attribute that is not xsi:nil. Missing, repeating, complex, or nil values are not supported."
                        : $"Source '{field.SourcePath}' must select at most one scalar element or attribute that is not xsi:nil. Repeating, complex, or nil values are not supported.")));
            }
            var prelude = new List<XElement>();
            var value = field.Expression is not null
                ? new XElement(Xsl + "value-of", new XAttribute("select", Compile(field.Expression, inner, folds, prelude)))
                : field.Constant is null
                    ? new XElement(Xsl + "value-of", new XAttribute("select", source!))
                    : new XElement(Xsl + "text", field.Constant);
            var initial = field.MissingValueBehavior == MissingXmlValueBehavior.Default
                ? new XElement(Xsl + "choose",
                    new XElement(Xsl + "when", new XAttribute("test", $"count({source}) = 0"), new XElement(Xsl + "text", field.DefaultValue)),
                    new XElement(Xsl + "otherwise", value))
                : value;
            element.Add(prelude);
            if (field.Operations.Count == 0) element.Add(initial);
            else
            {
                element.Add(new XElement(Xsl + "variable", new XAttribute("name", "value0"), initial));
                for (var index = 0; index < field.Operations.Count; index++)
                {
                    var operation = field.Operations[index];
                    var input = $"string($value{index})";
                    if (operation.Argument is not null)
                        element.Add(new XElement(Xsl + "variable", new XAttribute("name", $"argument{index}"), new XElement(Xsl + "text", operation.Argument)));
                    var expression = operation.Kind switch
                    {
                        XmlStringOperationKind.NormalizeWhitespace => $"normalize-space({input})",
                        XmlStringOperationKind.UppercaseAscii => $"translate({input}, 'abcdefghijklmnopqrstuvwxyz', 'ABCDEFGHIJKLMNOPQRSTUVWXYZ')",
                        XmlStringOperationKind.LowercaseAscii => $"translate({input}, 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz')",
                        XmlStringOperationKind.Prefix => $"concat(string($argument{index}), {input})",
                        XmlStringOperationKind.Suffix => $"concat({input}, string($argument{index}))",
                        _ => throw new ArgumentException("Unsupported XML string operation.")
                    };
                    element.Add(new XElement(Xsl + "variable", new XAttribute("name", $"value{index + 1}"), new XAttribute("select", expression)));
                }
                element.Add(new XElement(Xsl + "value-of", new XAttribute("select", $"$value{field.Operations.Count}")));
            }
        }
        foreach (var child in node.Children.OrderByDescending(static child => child.IsAttribute))
            element.Add(Emit(child, inner, scopeChecks, folds));
        XElement result = node.Assignment is { MissingValueBehavior: MissingXmlValueBehavior.Omit }
            ? new XElement(Xsl + "if", new XAttribute("test", node.Assignment.FirstOccurrence ? $"count({source}) >= 1" : $"count({source}) = 1"), element)
            : node.Assignment?.Condition is { } condition
                ? new XElement(Xsl + "if", new XAttribute("test", $"string({Compile(condition, inner, folds, null)}) = 'true'"), element)
                : element;
        if (node.RecordCondition is { } recordCondition)
        {
            // The source checks of a conditional record live inside its gate: a skipped record must not fail on sources it never reads.
            result = new XElement(Xsl + "if", new XAttribute("test", $"string({Compile(recordCondition, inner, folds, null)}) = 'true'"), scopeChecks, result);
            scopeChecks = [];
        }
        if (node.Repeat is null) return result;
        var loop = new XElement(Xsl + "for-each", new XAttribute("select", SourceExpression(node.Repeat.SourcePath, scopes)));
        loop.Add(new XElement(Xsl + "variable", new XAttribute("name", $"r{inner.Count}"), new XAttribute("select", ".")));
        loop.Add(new XElement(Xsl + "variable", new XAttribute("name", $"p{inner.Count}"), new XAttribute("select", "position()")));
        loop.Add(new XElement(Xsl + "if", new XAttribute("test", $"self::node(){NilTest}"),
            new XElement(Xsl + "message", new XAttribute("terminate", "yes"), $"Source record '{node.Repeat.SourcePath}' is xsi:nil, which is not supported.")));
        loop.Add(scopeChecks);
        loop.Add(result);
        return loop;
    }

    private static IReadOnlyList<(XName Name, bool IsAttribute)> ParsePath(string path, bool absolute, XmlMappingDefinition map)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') != absolute)
            throw new ArgumentException(absolute ? "Source paths must be absolute, e.g. /s:Order/s:Id." : "Target paths must be relative to the target root, e.g. t:Header/t:Id.");
        var segments = (absolute ? path[1..] : path).Split('/');
        if (segments.Length > 32) throw new ArgumentException("XML mapping paths cannot exceed 32 segments.");
        var result = new List<(XName Name, bool IsAttribute)>();
        for (var index = 0; index < segments.Length; index++)
        {
            var attribute = segments[index].StartsWith('@');
            if (attribute && (index != segments.Length - 1 || (absolute && index == 0)))
                throw new ArgumentException("An attribute may only be the final path segment, below a source root element.");
            result.Add((ResolveName(attribute ? segments[index][1..] : segments[index], map), attribute));
        }
        return result;
    }

    private static XName ResolveName(string name, XmlMappingDefinition map)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("XML mapping names cannot be empty.");
        var parts = name.Split(':');
        foreach (var part in parts) VerifyName(part);
        if (parts.Length == 1) return XName.Get(name);
        if (parts.Length != 2 || !map.Namespaces.TryGetValue(parts[0], out var uri))
            throw new ArgumentException($"XML name '{name}' must use a declared namespace prefix.");
        return XName.Get(parts[1], uri);
    }

    private static void VerifyName(string name)
    {
        try { XmlConvert.VerifyNCName(name); }
        catch (XmlException exception) { throw new ArgumentException($"Invalid XML mapping name '{name}'. Only element/attribute names are supported, not XPath expressions.", exception); }
    }

    private sealed class TargetNode(XName name, bool isAttribute = false)
    {
        public XName Name { get; } = name;
        public bool IsAttribute { get; } = isAttribute;
        public List<TargetNode> Children { get; } = [];
        public XmlFieldMappingDefinition? Assignment { get; set; }
        public XmlRepeatMappingDefinition? Repeat { get; set; }
        public XmlValueExpression? RecordCondition { get; set; }
    }

    private sealed class BoundedWriter(CancellationToken cancellationToken) : StringWriter(CultureInfo.InvariantCulture)
    {
        private void Check(int count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (GetStringBuilder().Length + count > 4_000_000)
                throw new ArgumentException("XML mapping output exceeds 4,000,000 characters.");
        }
        public override void Write(char value) { Check(1); base.Write(value); }
        public override void Write(string? value) { Check(value?.Length ?? 0); base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Check(count); base.Write(buffer, index, count); }
        public override void Write(ReadOnlySpan<char> buffer) { Check(buffer.Length); base.Write(buffer); }
    }
}
