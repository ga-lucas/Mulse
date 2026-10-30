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
}

public sealed record ParsedDataInspection(string ParsedData, IReadOnlyList<string> Fields, int RecordCount, IReadOnlyList<string>? Samples = null);

public static class ParsedDataMapping
{
    public const int MaxCharacters = 1_000_000;
    public const int MaxFields = 256;
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
        var targets = new List<string[]>();
        foreach (var field in map.Fields)
        {
            if (field is null) throw new ArgumentException("Mapping fields cannot be null.");
            Segments(field.SourcePath);
            var target = Segments(field.TargetPath);
            if (target.Length == 0) throw new ArgumentException("Every mapping needs a non-root target path.");
            if (field.Kind is not ("Field" or "Constant")) throw new ArgumentException($"Unknown mapping kind '{field.Kind}'.");
            if (field.Missing is not ("Error" or "Omit" or "Null")) throw new ArgumentException($"Unknown missing-field policy '{field.Missing}'.");
            if (field.Kind == "Constant") ParseJson(field.ConstantJson);
            if (targets.Any(other => other.Take(Math.Min(other.Length, target.Length)).SequenceEqual(target.Take(Math.Min(other.Length, target.Length)))))
                throw new ArgumentException($"Duplicate or overlapping target path '{field.TargetPath}'.");
            targets.Add(target);
        }
        return map;
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
    {
        cancellationToken.ThrowIfCancellationRequested();
        var map = ReadMap(mappingJson);
        if (map.Fields.Count == 0) throw new ArgumentException("Add at least one field mapping before previewing or running.");
        var data = Normalize(text, format);
        var rows = Rows(data, map.RecordPath);
        var result = new JsonArray();
        var length = 1;
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = new JsonObject();
            var valueCharacters = 0;
            foreach (var field in map.Fields)
            {
                JsonNode? value;
                if (field.Kind == "Constant") value = ParseJson(field.ConstantJson);
                else if (!TryRead(row, field.SourcePath, out value))
                {
                    if (field.Missing == "Omit") continue;
                    if (field.Missing == "Error") throw new ArgumentException($"Source field '{field.SourcePath}' is missing in record {result.Count + 1}.");
                    value = null;
                }
                valueCharacters += value?.ToJsonString().Length ?? 4;
                if (length + valueCharacters > 4_000_000)
                    throw new ArgumentException("Mapped output exceeds 4000000 characters.");
                Write(output, Segments(field.TargetPath), value);
            }
            length += output.ToJsonString().Length + 1;
            if (length > 4_000_000) throw new ArgumentException("Mapped output exceeds 4000000 characters.");
            result.Add(output);
        }
        return result.ToJsonString();
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

    private static JsonNode? ParseJson(string text)
    {
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
