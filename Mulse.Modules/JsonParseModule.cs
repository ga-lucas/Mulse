using System.Text.Json;

namespace Mulse.Modules;

public sealed class JsonParseModule : IParseModule
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        capabilities: [ModuleCapability.Parsing]);

    public ModuleDescriptor Descriptor { get; } = new(
        "json-parse",
        "JSON parse",
        ModuleKind.Parse,
        "Validates raw JSON payloads and normalizes them into the working integration payload form.",
        recommendation: Recommendation);

    public Task<IntegrationBatch> ParseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var parsedPayloads = batch.Payloads.Select(payload =>
        {
            using var document = JsonDocument.Parse(payload.Content);
            var normalizedJson = JsonSerializer.SerializeToUtf8Bytes(document.RootElement);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["parsedBy"] = Descriptor.Id,
                ["parsedFormat"] = "Json"
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".json"),
                BinaryData.FromBytes(normalizedJson),
                "application/json",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(parsedPayloads));
    }
}
