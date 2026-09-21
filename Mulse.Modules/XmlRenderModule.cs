using System.Text.Json.Nodes;
using System.Xml.Linq;

namespace Mulse.Modules;

public sealed class XmlRenderModule : IRenderModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("rootElement", "Root element", "The XML root element name.", false, ModuleSettingInputKind.Text, "Integration"),
        new("itemElement", "Array item element", "The XML element name used for array entries.", false, ModuleSettingInputKind.Text, "Item"),
        new("indent", "Indent output", "Writes indented XML when enabled.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml],
        capabilities: [ModuleCapability.Serialization]);

    public ModuleDescriptor Descriptor { get; } = new(
        "xml-render",
        "XML render",
        ModuleKind.Render,
        "Serializes the current working payload into XML for downstream delivery modules.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rootElement = ModuleSettingReader.GetOptional(step.Settings, "rootElement") ?? "Integration";
        var itemElement = ModuleSettingReader.GetOptional(step.Settings, "itemElement") ?? "Item";
        var indent = ModuleSettingReader.GetBoolean(step.Settings, "indent", defaultValue: true, Descriptor.Id);

        var renderedPayloads = batch.Payloads.Select(payload =>
        {
            var document = CreateDocument(payload, rootElement, itemElement);
            var xml = indent ? document.ToString() : document.ToString(SaveOptions.DisableFormatting);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id,
                ["renderedFormat"] = "Xml"
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".xml"),
                BinaryData.FromString(xml),
                "application/xml",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(renderedPayloads));
    }

    private static XDocument CreateDocument(IntegrationPayload payload, string rootElement, string itemElement)
    {
        var text = payload.GetText();
        if (payload.ContentType.Contains("xml", StringComparison.OrdinalIgnoreCase) || text.TrimStart().StartsWith('<'))
        {
            return XDocument.Parse(text, LoadOptions.PreserveWhitespace);
        }

        var node = JsonNode.Parse(text);
        return new XDocument(CreateElement(rootElement, node, itemElement));
    }

    private static XElement CreateElement(string elementName, JsonNode? node, string itemElement)
    {
        if (node is JsonObject jsonObject)
        {
            var element = new XElement(elementName);
            foreach (var property in jsonObject)
            {
                element.Add(CreateElement(property.Key, property.Value, itemElement));
            }

            return element;
        }

        if (node is JsonArray jsonArray)
        {
            var element = new XElement(elementName);
            foreach (var item in jsonArray)
            {
                element.Add(CreateElement(itemElement, item, itemElement));
            }

            return element;
        }

        return new XElement(elementName, JsonPayloadNavigator.ExtractScalarText(node) ?? string.Empty);
    }
}
