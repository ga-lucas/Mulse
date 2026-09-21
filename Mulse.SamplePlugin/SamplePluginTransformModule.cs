using System.Text.Json;
using Mulse.Modules;

namespace Mulse.SamplePlugin;

public sealed class SamplePluginTransformModule(TimeProvider timeProvider) : IOrchestrationAugmentModule
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        [ModuleProtocol.Plugin],
        [ModuleCapability.Transformation, ModuleCapability.Envelope]);

    public ModuleDescriptor Descriptor { get; } = new(
        "sample-plugin-augment",
        "Sample plugin augment",
        ModuleKind.OrchestrationAugment,
        "Demonstrates a hot-loadable external orchestration augment plugin by wrapping payloads with plugin-specific metadata.",
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
                ["pluginPackage"] = "Mulse.SamplePlugin",
                ["pluginTransform"] = Descriptor.Id,
                ["pluginTransformedAtUtc"] = transformedAt.ToString("O")
            };

            var envelope = new
            {
                flowId = context.FlowId,
                executionId = context.ExecutionId,
                plugin = Descriptor.Id,
                transformedAt,
                settings = step.Settings,
                payloadName = payload.Name,
                payloadContentType = payload.ContentType,
                metadata,
                payload = payload.Content.ToString()
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".plugin.json"),
                BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(envelope)),
                "application/json",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(transformedPayloads));
    }
}
