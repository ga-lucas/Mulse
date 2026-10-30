using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Xsl;
using Mulse.Modules.Mapping;

namespace Mulse.CompatibilityPack;

public sealed class XmlMappingEngine
{
    public const int MaxSampleCharacters = 1_000_000;
    private static readonly XNamespace Xsl = "http://www.w3.org/1999/XSL/Transform";
    private readonly XslCompiledTransform _transform = new();
    private readonly System.Xml.Schema.XmlSchemaSet? _sourceSchemas;
    private readonly System.Xml.Schema.XmlSchemaSet? _targetSchemas;
    public string Xslt { get; }

    public XmlMappingEngine(string mappingJson)
    {
        if (string.IsNullOrWhiteSpace(mappingJson) || mappingJson.Length > 262_144)
            throw new ArgumentException("XML mapping JSON is required and must not exceed 256 KiB characters.");
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
        Xslt = Generate(map);
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
            using (var writer = XmlWriter.Create(output, _transform.OutputSettings))
                _transform.Transform(reader, arguments: null, writer);
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

    private static string Generate(XmlMappingDefinition map)
    {
        if (map.Version is not (1 or 2))
            throw new ArgumentException($"XML mapping version {map.Version} is not supported.");
        if (map.Namespaces is null || map.Namespaces.Count > 32 || map.Fields is null || map.Fields.Count is < 1 or > 128)
            throw new ArgumentException("XML mappings require 1 to 128 fields and at most 32 namespace prefixes.");
        var stylesheet = new XElement(Xsl + "stylesheet", new XAttribute("version", "1.0"), new XAttribute(XNamespace.Xmlns + "xsl", Xsl));
        foreach (var (prefix, uri) in map.Namespaces)
        {
            VerifyName(prefix);
            if (prefix.StartsWith("xml", StringComparison.OrdinalIgnoreCase) || prefix == "xsl" || string.IsNullOrWhiteSpace(uri)
                || !Uri.TryCreate(uri, UriKind.Absolute, out _) || uri == Xsl.NamespaceName || uri == XNamespace.Xml.NamespaceName || uri == XNamespace.Xmlns.NamespaceName)
                throw new ArgumentException($"Invalid or reserved XML namespace prefix/URI '{prefix}'. Use explicit, non-reserved prefixes and absolute URIs.");
            stylesheet.Add(new XAttribute(XNamespace.Xmlns + prefix, uri));
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
            var segments = ParsePath(field.TargetPath, absolute: false, map);
            if (field.Constant is not null && !string.IsNullOrEmpty(field.SourcePath))
                throw new ArgumentException($"Mapping '{field.TargetPath}' must specify either a source path or a constant, not both.");
            if (!Enum.IsDefined(field.MissingValueBehavior)
                || (map.Version == 1 && (field.MissingValueBehavior != MissingXmlValueBehavior.Error || field.DefaultValue is not null))
                || (field.Constant is not null && (field.MissingValueBehavior != MissingXmlValueBehavior.Error || field.DefaultValue is not null))
                || (field.MissingValueBehavior == MissingXmlValueBehavior.Default ? field.DefaultValue is null : field.DefaultValue is not null))
                throw new ArgumentException($"Mapping '{field.TargetPath}' has an invalid missing-value policy. Omit/default requires version 2, a source path, and a default value only for the Default policy.");
            if (field.Constant is null)
            {
                ParsePath(field.SourcePath, absolute: true, map);
                var cardinality = field.MissingValueBehavior == MissingXmlValueBehavior.Error ? "!= 1" : "> 1";
                template.Add(new XElement(Xsl + "if", new XAttribute("test", $"count({field.SourcePath}) {cardinality} or {field.SourcePath}/*"),
                    new XElement(Xsl + "message", new XAttribute("terminate", "yes"), field.MissingValueBehavior == MissingXmlValueBehavior.Error
                        ? $"Source '{field.SourcePath}' must select exactly one scalar element or attribute. Missing, repeating, or complex values are not supported."
                        : $"Source '{field.SourcePath}' must select at most one scalar element or attribute. Repeating or complex values are not supported.")));
            }
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
            current.Assignment = field;
        }
        template.Add(Emit(target));
        stylesheet.Add(new XElement(Xsl + "output", new XAttribute("method", "xml"), new XAttribute("omit-xml-declaration", "yes"), new XAttribute("indent", "yes")), template);
        return stylesheet.ToString();
    }

    private static XElement Emit(TargetNode node)
    {
        var element = new XElement(Xsl + (node.IsAttribute ? "attribute" : "element"),
            new XAttribute("name", node.Name.LocalName), new XAttribute("namespace", node.Name.NamespaceName));
        if (node.Assignment is { } field)
        {
            var value = field.Constant is null
                ? new XElement(Xsl + "value-of", new XAttribute("select", field.SourcePath))
                : new XElement(Xsl + "text", field.Constant);
            element.Add(field.MissingValueBehavior == MissingXmlValueBehavior.Default
                ? new XElement(Xsl + "choose",
                    new XElement(Xsl + "when", new XAttribute("test", $"count({field.SourcePath}) = 0"), new XElement(Xsl + "text", field.DefaultValue)),
                    new XElement(Xsl + "otherwise", value))
                : value);
        }
        foreach (var child in node.Children.OrderByDescending(static child => child.IsAttribute))
            element.Add(Emit(child));
        return node.Assignment is { MissingValueBehavior: MissingXmlValueBehavior.Omit } optional
            ? new XElement(Xsl + "if", new XAttribute("test", $"count({optional.SourcePath}) = 1"), element)
            : element;
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
