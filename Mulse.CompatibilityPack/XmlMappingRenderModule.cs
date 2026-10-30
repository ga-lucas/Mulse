using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class XmlMappingRenderModule : IRenderModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "xml-mapping-render", "XML mapping render", ModuleKind.Render,
        "Maps scalar XML fields and constants using a saved, namespace-aware mapping definition with optional XSD validation. Configure it in Mapping Designer.",
        [new("mappingJson", "XML mapping", "Versioned mapping edited in Mapping Designer. Scalar paths use explicit missing-value policies; repetitions are not supported. Version 2 can retain source/target XSD validation.", true, ModuleSettingInputKind.TextArea)],
        new ModuleRecommendationProfile([ModuleDataFormat.Xml], capabilities: [ModuleCapability.Mapping, ModuleCapability.Serialization, ModuleCapability.Transformation]));

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var engine = new XmlMappingEngine(ModuleSettings.GetRequired(step.Settings, "mappingJson", Descriptor.Id));
        var payloads = batch.Payloads.Select(payload => new IntegrationPayload(
            Path.ChangeExtension(payload.Name, ".xml"), BinaryData.FromString(engine.Transform(payload.GetText(), cancellationToken)), "application/xml",
            new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id, ["renderedFormat"] = "Xml"
            })).ToArray();
        return Task.FromResult(new IntegrationBatch(payloads));
    }
}
