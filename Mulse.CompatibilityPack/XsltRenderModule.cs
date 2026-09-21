using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Xsl;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class XsltRenderModule : IRenderModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("xsltPath", "XSLT path", "The path to the XSLT stylesheet used to transform the XML payload.", true),
        new("parametersJson", "Parameters JSON", "An optional JSON object of XSLT parameters.", false, ModuleSettingInputKind.TextArea, "{}"),
        new("outputExtension", "Output extension", "The file extension for transformed payloads.", false, ModuleSettingInputKind.Text, ".xml")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Xml],
        capabilities: [ModuleCapability.Mapping, ModuleCapability.Serialization, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "xslt-render",
        "XSLT render",
        ModuleKind.Render,
        "Transforms XML payloads with XSLT so BizTalk-style map assets can be migrated into the render stage.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var xsltPath = Path.GetFullPath(ModuleSettings.GetRequired(step.Settings, "xsltPath", Descriptor.Id));
        var parameters = CompatibilityPayloadNavigator.ParseScalarObject(ModuleSettings.GetOptional(step.Settings, "parametersJson"), Descriptor.Id, "parametersJson");
        var outputExtension = ModuleSettings.GetOptional(step.Settings, "outputExtension") ?? ".xml";

        if (!File.Exists(xsltPath))
        {
            throw new FileNotFoundException($"XSLT file '{xsltPath}' was not found.", xsltPath);
        }

        var transform = new XslCompiledTransform();
        using (var stylesheetReader = XmlReader.Create(xsltPath, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit }))
        {
            transform.Load(stylesheetReader);
        }

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = CompatibilityPayloadNavigator.ParseXml(payload, Descriptor.Id);
            var transformed = Transform(transform, document, parameters);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id,
                ["renderedFormat"] = "Xslt"
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, outputExtension),
                BinaryData.FromString(transformed),
                string.Equals(outputExtension, ".xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" : "text/plain",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(payloads));
    }

    private static string Transform(XslCompiledTransform transform, System.Xml.Linq.XDocument document, IReadOnlyDictionary<string, string> parameters)
    {
        var arguments = new XsltArgumentList();
        foreach (var parameter in parameters)
        {
            arguments.AddParam(parameter.Key, string.Empty, parameter.Value);
        }

        using var stringWriter = new StringWriter();
        using var writer = XmlWriter.Create(stringWriter, transform.OutputSettings);
        using var reader = document.CreateReader();
        transform.Transform(reader, arguments, writer);
        writer.Flush();
        return stringWriter.ToString();
    }
}
