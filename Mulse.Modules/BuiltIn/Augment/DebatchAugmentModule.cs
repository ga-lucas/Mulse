using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace Mulse.Modules.BuiltIn.Augment;

/// <summary>
/// Splits ("debatches") each payload in the batch into multiple payloads. Unlike <c>xml-envelope-debatch-parse</c>
/// (which is XML-only and can only run at the parse stage), this module is format-agnostic (JSON array, XML
/// repeated elements, or delimited text) and can be placed anywhere in the augment chain, regardless of which
/// parse module produced the batch - mirroring the many places BizTalk allows debatching (receive pipeline
/// disassemblers, orchestration loops over envelope parts, etc.) with a single reusable, configuration-driven
/// module.
/// </summary>
public sealed class DebatchAugmentModule : IOrchestrationAugmentModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("mode", "Split mode", "How each payload should be split into multiple payloads.", true, ModuleSettingInputKind.Select, "JsonArray",
        [
            new ModuleSettingOption("JsonArray", "JSON array"),
            new ModuleSettingOption("XmlElements", "XML repeated elements"),
            new ModuleSettingOption("Delimited", "Delimited text")
        ]),
        new("jsonArrayPath", "JSON array path", "For JsonArray mode: a dot-separated path (e.g. 'data.items') to the array to split. Leave blank to split the payload's own top-level array. Intermediate segments may end in '[]' (e.g. 'orders[].items') to split a nested array once per element of an outer array, preserving parent correlation - use this for hierarchical/nested lists.", false),
        new("jsonParentIdField", "JSON parent ID field", "For JsonArray mode with a nested '[]' path: the property name on the nearest enclosing array element to record as 'debatchedParentId' metadata on each split part (e.g. 'orderId').", false),
        new("xmlElementXPath", "XML element XPath", "For XmlElements mode: an XPath expression selecting each repeated element to split out (e.g. //*[local-name()='Item']). If xmlParentXPath is set, this is evaluated relative to each matched parent element instead (e.g. 'Items/Item').", false),
        new("xmlParentXPath", "XML parent XPath", "For XmlElements mode with nested/hierarchical elements: an absolute XPath expression selecting each outer parent element (e.g. //Order). xmlElementXPath is then evaluated relative to each parent, and each split part is tagged with parent correlation metadata.", false),
        new("xmlParentIdXPath", "XML parent ID XPath", "For XmlElements mode with xmlParentXPath set: an XPath expression (relative to each parent element, e.g. '@id' or 'OrderId') identifying the parent, recorded as 'debatchedParentId' metadata on each split part.", false),
        new("delimiter", "Delimiter", "For Delimited mode: the text delimiter to split on. Defaults to a newline.", false, ModuleSettingInputKind.Text, "\n"),
        new("preserveMetadata", "Preserve source metadata", "Copies the original payload's metadata onto each split part, in addition to the debatch tracking fields this module always adds.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Envelope, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "debatch-augment",
        "Debatch (split messages)",
        ModuleKind.OrchestrationAugment,
        "Splits each payload in the batch into multiple payloads (JSON array, XML repeated elements, or delimited text). Usable anywhere in the augment chain.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var mode = ModuleSettingReader.GetOptional(step.Settings, "mode") ?? "JsonArray";
        var preserveMetadata = ModuleSettingReader.GetBoolean(step.Settings, "preserveMetadata", defaultValue: true, Descriptor.Id);

        var debatchedPayloads = new List<IntegrationPayload>(batch.Count);
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<DebatchedPart> parts = mode switch
            {
                "JsonArray" => SplitJsonArray(
                    payload,
                    ModuleSettingReader.GetOptional(step.Settings, "jsonArrayPath"),
                    ModuleSettingReader.GetOptional(step.Settings, "jsonParentIdField"),
                    Descriptor.Id),
                "XmlElements" => SplitXmlElements(
                    payload,
                    ModuleSettingReader.GetOptional(step.Settings, "xmlElementXPath"),
                    ModuleSettingReader.GetOptional(step.Settings, "xmlParentXPath"),
                    ModuleSettingReader.GetOptional(step.Settings, "xmlParentIdXPath"),
                    Descriptor.Id),
                "Delimited" => SplitDelimited(payload, ModuleSettingReader.GetOptional(step.Settings, "delimiter") ?? "\n"),
                _ => throw new ArgumentException($"Module '{Descriptor.Id}' has an unknown mode '{mode}'. Expected 'JsonArray', 'XmlElements', or 'Delimited'.", nameof(step))
            };

            for (var index = 0; index < parts.Count; index++)
            {
                var part = parts[index];
                var metadata = preserveMetadata
                    ? new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                metadata["debatchedBy"] = Descriptor.Id;
                metadata["debatchedFrom"] = payload.Name;
                metadata["debatchIndex"] = index.ToString();
                metadata["debatchCount"] = parts.Count.ToString();

                if (part.ParentPath is not null)
                {
                    metadata["debatchedParentPath"] = part.ParentPath;
                }

                if (part.ParentIndex.HasValue)
                {
                    metadata["debatchedParentIndex"] = part.ParentIndex.Value.ToString();
                }

                if (part.ParentId is not null)
                {
                    metadata["debatchedParentId"] = part.ParentId;
                }

                debatchedPayloads.Add(new IntegrationPayload(
                    $"{Path.GetFileNameWithoutExtension(payload.Name)}-{index}{GetExtension(part.ContentType)}",
                    part.Content,
                    part.ContentType,
                    metadata));
            }
        }

        return Task.FromResult(new IntegrationBatch(debatchedPayloads));
    }

    /// <summary>
    /// A single split-out payload, plus optional parent-correlation metadata for hierarchical/nested
    /// source lists (e.g. splitting 'items' nested inside each element of an outer 'orders' array).
    /// </summary>
    private readonly record struct DebatchedPart(
        BinaryData Content,
        string ContentType,
        string? ParentPath = null,
        int? ParentIndex = null,
        string? ParentId = null);

    /// <summary>
    /// A navigation context used while walking a (possibly nested) JSON path: the current node,
    /// the breadcrumb of ('propertyName', index) pairs recorded at each expanded ('[]') ancestor
    /// array, and the nearest exploded ancestor element (for parent-ID extraction).
    /// </summary>
    private readonly record struct JsonPathContext(JsonNode? Node, IReadOnlyList<(string Label, int Index)> Breadcrumb, JsonNode? NearestParentElement);

    private static IReadOnlyList<DebatchedPart> SplitJsonArray(IntegrationPayload payload, string? arrayPath, string? parentIdField, string moduleId)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(payload.GetText());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Module '{moduleId}' could not parse payload '{payload.Name}' as JSON.", exception);
        }

        var segments = string.IsNullOrWhiteSpace(arrayPath)
            ? []
            : arrayPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var contexts = new List<JsonPathContext>
        {
            new(root, [], null)
        };

        for (var segmentIndex = 0; segmentIndex < segments.Length; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            var isLastSegment = segmentIndex == segments.Length - 1;
            var expand = !isLastSegment && segment.EndsWith("[]", StringComparison.Ordinal);
            var propertyName = expand || segment.EndsWith("[]", StringComparison.Ordinal) ? segment[..^2] : segment;

            var nextContexts = new List<JsonPathContext>();
            foreach (var context in contexts)
            {
                var next = propertyName.Length == 0 ? context.Node : context.Node?[propertyName];
                if (next is null)
                {
                    throw new InvalidOperationException($"Module '{moduleId}' could not find JSON path '{arrayPath}' in payload '{payload.Name}'.");
                }

                if (expand)
                {
                    if (next is not JsonArray array)
                    {
                        throw new InvalidOperationException($"Module '{moduleId}' expected an array at '{propertyName}' (path '{arrayPath}') in payload '{payload.Name}'.");
                    }

                    for (var elementIndex = 0; elementIndex < array.Count; elementIndex++)
                    {
                        var element = array[elementIndex];
                        nextContexts.Add(new JsonPathContext(
                            element,
                            [.. context.Breadcrumb, (propertyName, elementIndex)],
                            element));
                    }
                }
                else
                {
                    nextContexts.Add(context with { Node = next });
                }
            }

            contexts = nextContexts;
        }

        var describedLocation = string.IsNullOrWhiteSpace(arrayPath) ? "the payload root" : $"'{arrayPath}'";
        var parts = new List<DebatchedPart>();

        foreach (var context in contexts)
        {
            if (context.Node is not JsonArray array)
            {
                throw new InvalidOperationException($"Module '{moduleId}' expected a JSON array at {describedLocation} in payload '{payload.Name}'.");
            }

            var parentPath = context.Breadcrumb.Count > 0
                ? string.Join('.', context.Breadcrumb.Select(static crumb => $"{crumb.Label}[{crumb.Index}]"))
                : null;
            var parentIndex = context.Breadcrumb.Count > 0 ? context.Breadcrumb[^1].Index : (int?)null;
            var parentId = !string.IsNullOrWhiteSpace(parentIdField) && context.NearestParentElement is not null
                ? JsonPayloadNavigator.ExtractScalarText(context.NearestParentElement[parentIdField])
                : null;

            foreach (var element in array)
            {
                parts.Add(new DebatchedPart(
                    BinaryData.FromString(element?.ToJsonString() ?? "null"),
                    "application/json",
                    parentPath,
                    parentIndex,
                    parentId));
            }
        }

        return parts;
    }

    private static IReadOnlyList<DebatchedPart> SplitXmlElements(IntegrationPayload payload, string? xpath, string? parentXPath, string? parentIdXPath, string moduleId)
    {
        if (string.IsNullOrWhiteSpace(xpath))
        {
            throw new ArgumentException($"Module '{moduleId}' requires the 'xmlElementXPath' setting when mode is 'XmlElements'.", nameof(xpath));
        }

        XDocument document;
        try
        {
            document = XDocument.Parse(payload.GetText(), LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException($"Module '{moduleId}' could not parse payload '{payload.Name}' as XML.", exception);
        }

        var parts = new List<DebatchedPart>();

        if (string.IsNullOrWhiteSpace(parentXPath))
        {
            IReadOnlyList<XElement> elements;
            try
            {
                elements = document.XPathSelectElements(xpath).ToArray();
            }
            catch (XPathException exception)
            {
                throw new ArgumentException($"Module '{moduleId}' has an invalid xmlElementXPath expression '{xpath}'.", nameof(xpath), exception);
            }

            if (elements.Count == 0)
            {
                throw new InvalidOperationException($"Module '{moduleId}' found no elements matching xmlElementXPath '{xpath}' in payload '{payload.Name}'.");
            }

            parts.AddRange(elements.Select(static element =>
                new DebatchedPart(BinaryData.FromString(element.ToString(SaveOptions.DisableFormatting)), "application/xml")));
            return parts;
        }

        IReadOnlyList<XElement> parentElements;
        try
        {
            parentElements = document.XPathSelectElements(parentXPath).ToArray();
        }
        catch (XPathException exception)
        {
            throw new ArgumentException($"Module '{moduleId}' has an invalid xmlParentXPath expression '{parentXPath}'.", nameof(parentXPath), exception);
        }

        if (parentElements.Count == 0)
        {
            throw new InvalidOperationException($"Module '{moduleId}' found no elements matching xmlParentXPath '{parentXPath}' in payload '{payload.Name}'.");
        }

        for (var parentIndex = 0; parentIndex < parentElements.Count; parentIndex++)
        {
            var parentElement = parentElements[parentIndex];
            IReadOnlyList<XElement> childElements;
            try
            {
                childElements = parentElement.XPathSelectElements(xpath).ToArray();
            }
            catch (XPathException exception)
            {
                throw new ArgumentException($"Module '{moduleId}' has an invalid xmlElementXPath expression '{xpath}'.", nameof(xpath), exception);
            }

            var parentId = string.IsNullOrWhiteSpace(parentIdXPath) ? null : ExtractXPathScalar(parentElement, parentIdXPath, moduleId);
            var parentPath = $"{parentElement.Name.LocalName}[{parentIndex}]";

            foreach (var childElement in childElements)
            {
                parts.Add(new DebatchedPart(
                    BinaryData.FromString(childElement.ToString(SaveOptions.DisableFormatting)),
                    "application/xml",
                    parentPath,
                    parentIndex,
                    parentId));
            }
        }

        if (parts.Count == 0)
        {
            throw new InvalidOperationException($"Module '{moduleId}' found no elements matching xmlElementXPath '{xpath}' under any element matching xmlParentXPath '{parentXPath}' in payload '{payload.Name}'.");
        }

        return parts;
    }

    private static string? ExtractXPathScalar(XElement context, string xpath, string moduleId)
    {
        object result;
        try
        {
            result = context.XPathEvaluate(xpath);
        }
        catch (XPathException exception)
        {
            throw new ArgumentException($"Module '{moduleId}' has an invalid XPath expression '{xpath}'.", nameof(xpath), exception);
        }

        return result switch
        {
            IEnumerable<object> nodes => nodes.Select(static node => node switch
            {
                XAttribute attribute => attribute.Value,
                XElement element => element.Value,
                _ => node?.ToString()
            }).FirstOrDefault(),
            string text => text,
            bool boolean => boolean.ToString(),
            double number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => result?.ToString()
        };
    }

    private static IReadOnlyList<DebatchedPart> SplitDelimited(IntegrationPayload payload, string delimiter)
    {
        var actualDelimiter = delimiter.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t");
        var text = payload.GetText();
        var parts = text.Split(actualDelimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts
            .Select(part => new DebatchedPart(BinaryData.FromString(part), payload.ContentType))
            .ToArray();
    }

    private static string GetExtension(string contentType) => contentType switch
    {
        "application/json" => ".json",
        "application/xml" => ".xml",
        _ => string.Empty
    };
}
