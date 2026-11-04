using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace Mulse.Modules.Mapping;

public sealed class ParsedDataMap
{
    public int Version { get; set; } = 1;
    public string RecordPath { get; set; } = string.Empty;
    public List<string> InputFields { get; set; } = [];
    public List<string> OutputFields { get; set; } = [];
    public List<ParsedDataFieldMap> Fields { get; set; } = [];
}

public sealed class ParsedDataFieldMap
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string Kind { get; set; } = "Field";
    public string ConstantJson { get; set; } = "null";
    public string Missing { get; set; } = "Error";
    public string TransformModule { get; set; } = string.Empty;
    public Dictionary<string, string> TransformSettings { get; set; } = [];
    public List<ParsedDataTransformInput> TransformInputs { get; set; } = [];
    public string SourceScope { get; set; } = "Current";
    public List<ParsedDataFieldMap> Fields { get; set; } = [];
    public List<string> InputFields { get; set; } = [];
    public List<string> OutputFields { get; set; } = [];
    public List<ParsedDataCondition> Conditions { get; set; } = [];
    public string ConditionMatch { get; set; } = "All";
    public string Otherwise { get; set; } = "Omit";
    public string OtherwiseJson { get; set; } = "null";
    public List<ParsedDataTransformStep> TransformChain { get; set; } = [];
}

public sealed class ParsedDataTransformInput
{
    public string Kind { get; set; } = "Field";
    public string SourcePath { get; set; } = string.Empty;
    public string ConstantJson { get; set; } = "null";
    public string SourceScope { get; set; } = "Current";
}

public sealed class ParsedDataTransformStep
{
    public string Module { get; set; } = string.Empty;
    public Dictionary<string, string> Settings { get; set; } = [];
    public List<ParsedDataTransformInput> Inputs { get; set; } = [new() { Kind = "Previous" }];
}

public sealed class ParsedDataCondition
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceScope { get; set; } = "Current";
    public string Operator { get; set; } = "Exists";
    public string ValueJson { get; set; } = "null";
}

public sealed record ParsedDataInspection(string ParsedData, IReadOnlyList<string> Fields, int RecordCount, IReadOnlyList<string>? Samples = null);

public static class ParsedDataMapping
{
    public const int MaxCharacters = 1_000_000;
    public const int MaxFields = 256;
    public const int MaxMappingDepth = 8;
    public const int MaxChainSteps = 16;
    public const int MaxConditions = 32;
    public const int MaxOperations = 100_000;
    public const int MaxOutputCharacters = 4_000_000;
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ParsedDataMap ReadMap(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 262_144)
            throw new ArgumentException("Supply a mapping document of at most 262144 characters.");
        ParsedDataMap map;
        try { map = JsonSerializer.Deserialize<ParsedDataMap>(json, JsonOptions) ?? throw new ArgumentException("Mapping must be an object."); }
        catch (JsonException ex) { throw new ArgumentException($"Invalid parsed-data mapping: {ex.Message}", ex); }
        if (map.Version != 1) throw new ArgumentException("Unsupported parsed-data mapping version.");
        if (map.Fields is null || map.InputFields is null || map.OutputFields is null
            || map.Fields.Count > MaxFields || map.InputFields.Count > MaxFields || map.OutputFields.Count > MaxFields)
            throw new ArgumentException($"Mapping lists must contain at most {MaxFields} fields.");
        Segments(map.RecordPath);
        foreach (var path in map.InputFields.Concat(map.OutputFields)) Segments(path);
        var count = 0;
        ValidateFields(map.Fields, 1, 0, ref count);
        return map;
    }

    private static void ValidateFields(List<ParsedDataFieldMap> fields, int depth, int outputDepth, ref int count)
    {
        if (depth > MaxMappingDepth) throw new ArgumentException($"Mapping nesting exceeds {MaxMappingDepth} levels.");
        var targets = new List<string[]>();
        foreach (var field in fields)
        {
            if (field is null) throw new ArgumentException("Mapping fields cannot be null.");
            if (++count > MaxFields) throw new ArgumentException($"Mapping supports at most {MaxFields} fields across all nesting levels.");
            Segments(field.SourcePath);
            ValidateScope(field.SourceScope);
            var target = Segments(field.TargetPath);
            if (target.Length == 0) throw new ArgumentException("Every mapping needs a non-root target path.");
            if (outputDepth + target.Length > 32) throw new ArgumentException("Nested output paths exceed 32 levels.");
            if (field.Kind is not ("Field" or "Constant" or "Transform" or "Object" or "Array")) throw new ArgumentException($"Unknown mapping kind '{field.Kind}'.");
            if (field.Missing is not ("Error" or "Omit" or "Null")) throw new ArgumentException($"Unknown missing-field policy '{field.Missing}'.");
            if (field.Kind == "Constant") ParseJson(field.ConstantJson);
            if (field.Fields is null || field.Conditions is null || field.Conditions.Count > MaxConditions
                || field.TransformChain is null || field.TransformChain.Count > MaxChainSteps)
                throw new ArgumentException($"Child fields, conditions and transform chains must be non-null; at most {MaxConditions} conditions and {MaxChainSteps} chain steps are supported.");
            if (field.InputFields is null || field.OutputFields is null || field.InputFields.Count > MaxFields || field.OutputFields.Count > MaxFields)
                throw new ArgumentException($"Nested field lists must contain at most {MaxFields} paths.");
            foreach (var path in field.InputFields.Concat(field.OutputFields)) Segments(path);
            if (field.ConditionMatch is not ("All" or "Any")) throw new ArgumentException("Condition match must be All or Any.");
            if (field.Otherwise is not ("Omit" or "Null" or "Constant")) throw new ArgumentException("Otherwise must be Omit, Null or Constant.");
            if (field.Otherwise == "Constant") ParseJson(field.OtherwiseJson);
            foreach (var condition in field.Conditions)
            {
                if (condition is null) throw new ArgumentException("Conditions cannot be null.");
                Segments(condition.SourcePath);
                ValidateScope(condition.SourceScope);
                if (condition.Operator is not ("Exists" or "Missing" or "IsNull" or "IsNotNull" or "Equals" or "NotEquals"
                    or "GreaterThan" or "GreaterOrEqual" or "LessThan" or "LessOrEqual" or "Contains" or "StartsWith" or "EndsWith"))
                    throw new ArgumentException($"Unknown condition operator '{condition.Operator}'.");
                if (condition.Operator is not ("Exists" or "Missing" or "IsNull" or "IsNotNull")) ParseJson(condition.ValueJson);
            }
            ValidateTransform(field.TransformModule, field.TransformSettings, field.TransformInputs, false, field.Kind == "Transform");
            foreach (var step in field.TransformChain)
            {
                if (step is null) throw new ArgumentException("Transform chain steps cannot be null.");
                ValidateTransform(step.Module, step.Settings, step.Inputs, true, true);
            }
            if (targets.Any(other => other.Take(Math.Min(other.Length, target.Length)).SequenceEqual(target.Take(Math.Min(other.Length, target.Length)))))
                throw new ArgumentException($"Duplicate or overlapping target path '{field.TargetPath}'.");
            targets.Add(target);
            if (field.Fields.Count > 0) ValidateFields(field.Fields, depth + 1, outputDepth + target.Length + (field.Kind == "Array" ? 1 : 0), ref count);
        }
    }

    private static void ValidateScope(string scope)
    {
        if (scope is not ("Current" or "Parent" or "Root")) throw new ArgumentException($"Unknown source scope '{scope}'.");
    }

    private static void ValidateTransform(string module, Dictionary<string, string> settings, List<ParsedDataTransformInput> inputs, bool allowPrevious, bool active)
    {
        if (inputs is null || inputs.Count > MaxFields || settings is null || settings.Any(setting => setting.Value is null))
            throw new ArgumentException($"Transform inputs must contain at most {MaxFields} values and settings must be non-null.");
        if (!active) return;
        if (string.IsNullOrWhiteSpace(module)) throw new ArgumentException("Choose a transform module for every transform mapping or chain step.");
        foreach (var input in inputs)
        {
            if (input is null || (input.Kind is not ("Field" or "Constant") && !(allowPrevious && input.Kind == "Previous")))
                throw new ArgumentException("Transform inputs must be fields or JSON constants (or Previous for chain steps); nested transforms are not supported.");
            Segments(input.SourcePath);
            ValidateScope(input.SourceScope);
            if (input.Kind == "Constant") ParseJson(input.ConstantJson);
        }
    }

    public static IEnumerable<string> TransformModules(ParsedDataMap map) => TransformModules(map.Fields);

    private static IEnumerable<string> TransformModules(IEnumerable<ParsedDataFieldMap> fields)
    {
        foreach (var field in fields)
        {
            if (field.Kind == "Transform") yield return field.TransformModule;
            foreach (var step in field.TransformChain) yield return step.Module;
            if (field.Kind is "Object" or "Array")
                foreach (var module in TransformModules(field.Fields)) yield return module;
        }
    }

    public static JsonNode? Normalize(string text, string format = "Auto")
    {
        if (text is null || text.Length > MaxCharacters) throw new ArgumentException($"Parsed data must be at most {MaxCharacters} characters.");
        if (format == "Auto") format = text.TrimStart().StartsWith('<') ? "Xml" : "Json";
        if (format == "Text") return CheckNormalizedSize(JsonValue.Create(text));
        if (format == "Json") return CheckNormalizedSize(ParseJson(text));
        if (format != "Xml") throw new ArgumentException($"Unknown parsed-data format '{format}'.");
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxCharacters
            });
            var root = XDocument.Load(reader, LoadOptions.PreserveWhitespace).Root ?? throw new ArgumentException("XML needs a root element.");
            return CheckNormalizedSize(new JsonObject { [root.Name.ToString()] = XmlValue(root, 0) });
        }
        catch (XmlException ex) { throw new ArgumentException($"Invalid parsed XML: {ex.Message}", ex); }
    }

    private static JsonNode? CheckNormalizedSize(JsonNode? node)
    {
        if ((node?.ToJsonString().Length ?? 4) > MaxCharacters)
            throw new ArgumentException($"Normalized parsed data exceeds {MaxCharacters} characters. Use a smaller input.");
        return node;
    }

    public static ParsedDataInspection Inspect(string text, string format, string recordPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = Normalize(text, format);
        var rows = Rows(data, recordPath);
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows) Discover(row, string.Empty, fields, 0, cancellationToken);
        return new(data?.ToJsonString() ?? "null", fields.Order(StringComparer.Ordinal).ToArray(), rows.Count);
    }

    public static string Map(string mappingJson, string text, string format = "Auto", CancellationToken cancellationToken = default)
        => MapAsync(mappingJson, text, format, cancellationToken).GetAwaiter().GetResult();

    public static async Task<string> MapAsync(string mappingJson, string text, string format = "Auto", CancellationToken cancellationToken = default,
        IModuleInvoker? moduleInvoker = null, FlowExecutionContext? context = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var map = ReadMap(mappingJson);
        if (map.Fields.Count == 0) throw new ArgumentException("Add at least one field mapping before previewing or running.");
        if (TransformModules(map).Any() && (moduleInvoker is null || context is null))
            throw new InvalidOperationException("Transform mappings require a module invoker and execution context. Use MapAsync with the host's module invoker.");
        var rows = Rows(Normalize(text, format), map.RecordPath);
        var state = new MappingState(moduleInvoker, context, cancellationToken);
        var result = new JsonArray();
        var length = 1;
        foreach (var row in rows)
        {
            var output = await MapObjectAsync(map.Fields, new(row, row, row), state, $"record {result.Count + 1}").ConfigureAwait(false);
            length += output.ToJsonString().Length + 1;
            CheckOutputSize(length);
            result.Add(output);
        }
        return result.ToJsonString();
    }

    private sealed record MappingScope(JsonNode? Current, JsonNode? Parent, JsonNode? Root)
    {
        public JsonNode? Select(string scope) => scope switch { "Parent" => Parent, "Root" => Root, _ => Current };
        public MappingScope Child(JsonNode? node) => new(node, Current, Root);
    }

    private sealed class MappingState(IModuleInvoker? invoker, FlowExecutionContext? context, CancellationToken cancellationToken)
    {
        private int _operations, _valueCharacters;
        public void Tick()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_operations > MaxOperations) throw new ArgumentException($"Mapping execution exceeds {MaxOperations} operations. Use a smaller input or a simpler mapping.");
        }
        public void ChargeValue(JsonNode? value)
        {
            _valueCharacters += value?.ToJsonString().Length ?? 4;
            CheckOutputSize(_valueCharacters);
        }
        public async Task<JsonNode?> InvokeAsync(string module, List<JsonNode?> inputs, Dictionary<string, string> settings)
        {
            Tick();
            var value = await invoker!.InvokeTransformAsync(module, context!, inputs, settings, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            CheckOutputSize(value?.ToJsonString().Length ?? 4);
            return value;
        }
    }

    private static void CheckOutputSize(int length)
    {
        if (length > MaxOutputCharacters) throw new ArgumentException($"Mapped output exceeds {MaxOutputCharacters} characters.");
    }

    private static async Task<JsonObject> MapObjectAsync(List<ParsedDataFieldMap> fields, MappingScope scope, MappingState state, string location)
    {
        var output = new JsonObject();
        state.Tick();
        foreach (var field in fields)
        {
            state.Tick();
            var matches = field.Conditions.Count == 0 || (field.ConditionMatch == "All"
                ? field.Conditions.All(condition => Matches(condition, scope, state))
                : field.Conditions.Any(condition => Matches(condition, scope, state)));
            JsonNode? value;
            if (!matches)
            {
                if (field.Otherwise == "Omit") continue;
                value = field.Otherwise == "Constant" ? ParseJson(field.OtherwiseJson) : null;
                state.ChargeValue(value);
            }
            else
            {
                var resolved = await ResolveFieldAsync(field, scope, state, location + " target " + field.TargetPath).ConfigureAwait(false);
                if (!resolved.Include) continue;
                value = resolved.Value;
                if (field.Kind is not ("Object" or "Array") || field.TransformChain.Count > 0) state.ChargeValue(value);
            }
            Write(output, Segments(field.TargetPath), value);
        }
        CheckOutputSize(output.ToJsonString().Length);
        return output;
    }

    private static async Task<(bool Include, JsonNode? Value)> ResolveFieldAsync(ParsedDataFieldMap field, MappingScope scope, MappingState state, string location)
    {
        JsonNode? value;
        if (field.Kind == "Transform")
        {
            var inputs = ResolveInputs(field.TransformInputs, scope, null, field.Missing, state, location);
            if (inputs is null) return (false, null);
            value = await state.InvokeAsync(field.TransformModule, inputs, field.TransformSettings).ConfigureAwait(false);
        }
        else
        {
            if (!ReadValue(scope, field.Kind, field.SourceScope, field.SourcePath, field.ConstantJson, null, field.Missing, location, out value)) return (false, null);
            if (field.Kind == "Object" && value is not null)
                value = await MapObjectAsync(field.Fields, scope.Child(value), state, location).ConfigureAwait(false);
            else if (field.Kind == "Array" && value is not null)
            {
                if (value is not JsonArray source) throw new ArgumentException($"Source field '{field.SourcePath}' must be an array at {location}.");
                var array = new JsonArray();
                var length = 1;
                foreach (var item in source)
                {
                    var mapped = await MapObjectAsync(field.Fields, scope.Child(item), state, location + $" item {array.Count + 1}").ConfigureAwait(false);
                    length += mapped.ToJsonString().Length + 1;
                    CheckOutputSize(length);
                    array.Add(mapped);
                }
                value = array;
            }
        }
        foreach (var step in field.TransformChain)
        {
            var inputs = ResolveInputs(step.Inputs, scope, value, field.Missing, state, location);
            if (inputs is null) return (false, null);
            value = await state.InvokeAsync(step.Module, inputs, step.Settings).ConfigureAwait(false);
        }
        return (true, value);
    }

    private static List<JsonNode?>? ResolveInputs(List<ParsedDataTransformInput> definitions, MappingScope scope, JsonNode? previous, string missing, MappingState state, string location)
    {
        var inputs = new List<JsonNode?>(definitions.Count);
        foreach (var input in definitions)
        {
            state.Tick();
            if (!ReadValue(scope, input.Kind, input.SourceScope, input.SourcePath, input.ConstantJson, previous, missing, location, out var value)) return null;
            inputs.Add(value?.DeepClone());
        }
        return inputs;
    }

    private static bool Matches(ParsedDataCondition condition, MappingScope scope, MappingState state)
    {
        state.Tick();
        var exists = TryRead(scope.Select(condition.SourceScope), condition.SourcePath, out var value);
        if (condition.Operator == "Exists") return exists;
        if (condition.Operator == "Missing") return !exists;
        if (!exists) return false;
        if (condition.Operator == "IsNull") return value is null;
        if (condition.Operator == "IsNotNull") return value is not null;
        var expected = ParseJson(condition.ValueJson);
        if (condition.Operator == "Equals") return JsonNode.DeepEquals(value, expected);
        if (condition.Operator == "NotEquals") return !JsonNode.DeepEquals(value, expected);
        if (condition.Operator is "Contains" or "StartsWith" or "EndsWith")
        {
            if (value is not JsonValue actual || !actual.TryGetValue<string>(out var text)
                || expected is not JsonValue literal || !literal.TryGetValue<string>(out var search))
                throw new ArgumentException($"Condition '{condition.Operator}' requires string values at '{condition.SourcePath}'.");
            return condition.Operator switch
            {
                "Contains" => text.Contains(search, StringComparison.Ordinal),
                "StartsWith" => text.StartsWith(search, StringComparison.Ordinal),
                _ => text.EndsWith(search, StringComparison.Ordinal)
            };
        }
        var left = TransformValues.ToNumber(value) ?? throw new ArgumentException($"Numeric condition requires a number at '{condition.SourcePath}'.");
        var right = TransformValues.ToNumber(expected) ?? throw new ArgumentException("Numeric condition requires a numeric comparison value.");
        return condition.Operator switch
        {
            "GreaterThan" => left > right, "GreaterOrEqual" => left >= right, "LessThan" => left < right, _ => left <= right
        };
    }

    public static string Escape(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    private static string[] Segments(string path)
    {
        if (path is null || path.Length > 4096) throw new ArgumentException("Field paths must be non-null and at most 4096 characters.");
        if (path.Length == 0) return [];
        if (!path.StartsWith('/')) throw new ArgumentException($"Path '{path}' must be a JSON Pointer starting with '/' (or empty for the whole record).");
        var parts = path[1..].Split('/');
        if (parts.Length > 32) throw new ArgumentException("Field paths may contain at most 32 segments.");
        foreach (var part in parts)
            for (var i = 0; i < part.Length; i++)
                if (part[i] == '~' && (++i >= part.Length || part[i] is not ('0' or '1')))
                    throw new ArgumentException($"Invalid JSON Pointer escape in '{path}'.");
        return parts.Select(part => part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
    }

    private static bool TryRead(JsonNode? node, string path, out JsonNode? value)
    {
        foreach (var part in Segments(path))
        {
            if (node is JsonObject obj && obj.TryGetPropertyValue(part, out var child)) node = child;
            else if (node is JsonArray array && (part.Length == 1 || !part.StartsWith('0'))
                && int.TryParse(part, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < array.Count) node = array[index];
            else { value = null; return false; }
        }
        value = node;
        return true;
    }

    private static IReadOnlyList<JsonNode?> Rows(JsonNode? data, string recordPath)
    {
        if (!TryRead(data, recordPath, out var selected)) throw new ArgumentException($"Record path '{recordPath}' was not found.");
        return selected is JsonArray array ? array.ToArray() : [selected];
    }

    private static void Write(JsonObject output, string[] parts, JsonNode? value)
    {
        var current = output;
        foreach (var part in parts[..^1])
        {
            if (current[part] is not JsonObject child) current[part] = child = new JsonObject();
            current = child;
        }
        current[parts[^1]] = value?.DeepClone();
    }

    private static bool ReadValue(MappingScope scope, string kind, string sourceScope, string path, string constantJson, JsonNode? previous, string missing, string location, out JsonNode? value)
    {
        if (kind == "Constant") { value = ParseJson(constantJson); return true; }
        if (kind == "Previous") { value = previous; return true; }
        if (TryRead(scope.Select(sourceScope), path, out value)) return true;
        if (missing == "Error") throw new ArgumentException($"Source field '{path}' is missing in {location}.");
        value = null;
        return missing != "Omit";
    }

    private static JsonNode? ParseJson(string text)
    {
        if (text is null) throw new ArgumentException("JSON constants must be non-null strings; use 'null' for JSON null.");
        try { return JsonNode.Parse(text); }
        catch (JsonException ex) { throw new ArgumentException($"Invalid JSON: {ex.Message}", ex); }
    }

    private static JsonNode XmlValue(XElement element, int depth)
    {
        if (depth > 32) throw new ArgumentException("XML nesting exceeds 32 levels.");
        if (!element.HasElements && !element.Attributes().Any(attribute => !attribute.IsNamespaceDeclaration)) return JsonValue.Create(element.Value)!;
        var result = new JsonObject();
        foreach (var attribute in element.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration))
            result["@" + attribute.Name] = attribute.Value;
        foreach (var group in element.Elements().GroupBy(child => child.Name))
        {
            var values = group.Select(child => XmlValue(child, depth + 1)).ToArray();
            result[group.Key.ToString()] = values.Length == 1 ? values[0] : new JsonArray(values.Cast<JsonNode?>().ToArray());
        }
        var text = string.Concat(element.Nodes().OfType<XText>().Select(node => node.Value));
        if (text.Length > 0 && (!element.HasElements || !string.IsNullOrWhiteSpace(text))) result["#text"] = text;
        return result;
    }

    private static void Discover(JsonNode? node, string path, HashSet<string> fields, int depth, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (depth > 32) throw new ArgumentException("Field discovery exceeds 32 levels.");
        fields.Add(path);
        if (fields.Count > MaxFields) throw new ArgumentException($"Sample has more than {MaxFields} discoverable paths. Supply a smaller sample or enter fields manually.");
        if (node is JsonObject obj)
            foreach (var property in obj) Discover(property.Value, path + "/" + Escape(property.Key), fields, depth + 1, cancellationToken);
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                Discover(array[index], path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture), fields, depth + 1, cancellationToken);
    }
}
