using System.Text.Json;

namespace Mulse.Modules;

public sealed class JsonEnvelopeAugmentTransformModule(TimeProvider timeProvider) : IOrchestrationAugmentModule
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        capabilities: [ModuleCapability.Transformation, ModuleCapability.Envelope]);

    public ModuleDescriptor Descriptor { get; } = new(
        "json-envelope-augment",
        "JSON orchestration augment",
        ModuleKind.OrchestrationAugment,
        "Wraps payloads in a JSON envelope with metadata and arbitrary configuration-driven augmentation fields.",
        recommendation: Recommendation);

    public Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var transformedPayloads = batch.Payloads.Select(payload =>
        {
            var transformedAt = timeProvider.GetUtcNow();
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["flowId"] = context.FlowId,
                ["executionId"] = context.ExecutionId,
                ["transformedBy"] = Descriptor.Id,
                ["transformedAtUtc"] = transformedAt.ToString("O")
            };

            var envelope = new
            {
                flowId = context.FlowId,
                executionId = context.ExecutionId,
                payloadName = payload.Name,
                payloadContentType = payload.ContentType,
                transformedAt,
                metadata,
                augmentation = step.Settings,
                payload = payload.Content.ToString()
            };

            var json = JsonSerializer.SerializeToUtf8Bytes(envelope);

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".json"),
                BinaryData.FromBytes(json),
                "application/json",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(transformedPayloads));
    }
}
