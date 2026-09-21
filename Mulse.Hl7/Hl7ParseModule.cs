using Mulse.Modules;

namespace Mulse.Hl7;

public sealed class Hl7ParseModule : IParseModule
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Parsing, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "hl7-parse",
        "HL7 parse",
        ModuleKind.Parse,
        "Parses HL7 v2 ER7 text messages into normalized JSON while promoting basic message metadata.",
        recommendation: Recommendation);

    public Task<IntegrationBatch> ParseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var message = Hl7Codec.Parse(payload.Content.ToString());
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["parsedBy"] = Descriptor.Id,
                ["parsedFormat"] = "Hl7"
            };

            foreach (var entry in Hl7Codec.CreateMetadata(message))
            {
                metadata[entry.Key] = entry.Value;
            }

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".json"),
                BinaryData.FromString(Hl7Codec.Serialize(message)),
                "application/json",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(payloads));
    }
}
