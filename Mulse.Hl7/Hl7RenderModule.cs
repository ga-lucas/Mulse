using Mulse.Modules;

namespace Mulse.Hl7;

public sealed class Hl7RenderModule : IRenderModule
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Serialization, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "hl7-render",
        "HL7 render",
        ModuleKind.Render,
        "Renders normalized HL7 JSON payloads back into HL7 v2 ER7 text messages.",
        recommendation: Recommendation);

    public Task<IntegrationBatch> RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = Hl7Codec.Deserialize(payload.Content.ToString());
            var rendered = Hl7Codec.Render(message);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id,
                ["renderedFormat"] = "Hl7"
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".hl7"),
                BinaryData.FromString(rendered),
                "text/hl7-v2",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(payloads));
    }
}
