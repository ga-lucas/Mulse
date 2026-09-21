using System.Text.Json;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class MetadataPromotionAugmentModule : IOrchestrationAugmentModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("promotionsJson", "Promotions JSON", "A JSON array describing payload selectors to promote into metadata.", true, ModuleSettingInputKind.TextArea,
            "[{\"metadataKey\":\"documentType\",\"selector\":\"$.header.type\"},{\"metadataKey\":\"messageId\",\"selector\":\"string(/default:Order/default:Header/default:MessageId)\"}]"),
        new("namespacesJson", "Namespaces JSON", "Optional JSON object of XML namespace prefix mappings used by XPath selectors.", false, ModuleSettingInputKind.TextArea, "{}"),
        new("overwriteExisting", "Overwrite existing metadata", "Replaces an existing metadata value when enabled.", false, ModuleSettingInputKind.Boolean, "false")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Enrichment, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "metadata-promotion-augment",
        "Metadata promotion augment",
        ModuleKind.OrchestrationAugment,
        "Promotes JSON-path or XPath-selected values into payload metadata so routing and downstream steps can act on them declaratively.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var promotionsJson = ModuleSettings.GetRequired(step.Settings, "promotionsJson", Descriptor.Id);
        var namespacesJson = ModuleSettings.GetOptional(step.Settings, "namespacesJson");
        var overwriteExisting = ModuleSettings.GetBoolean(step.Settings, "overwriteExisting", defaultValue: false, Descriptor.Id);
        var definitions = JsonSerializer.Deserialize<IReadOnlyList<PromotionDefinition>>(promotionsJson, SerializerOptions)
            ?? throw new InvalidOperationException($"Module '{Descriptor.Id}' requires a non-empty promotionsJson array.");

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase);
            foreach (var definition in definitions)
            {
                if (string.IsNullOrWhiteSpace(definition.MetadataKey) || string.IsNullOrWhiteSpace(definition.Selector))
                {
                    throw new InvalidOperationException($"Module '{Descriptor.Id}' requires every promotion to include metadataKey and selector.");
                }

                var values = CompatibilityPayloadNavigator.SelectValues(payload, definition.Selector, Descriptor.Id, namespacesJson);
                var resolvedValue = values.Count > 0
                    ? string.Join(definition.JoinWith ?? ";", values)
                    : definition.DefaultValue;

                if (string.IsNullOrWhiteSpace(resolvedValue))
                {
                    continue;
                }

                if (overwriteExisting || !metadata.ContainsKey(definition.MetadataKey))
                {
                    metadata[definition.MetadataKey] = resolvedValue;
                }
            }

            metadata["promotedBy"] = Descriptor.Id;
            return payload with { Metadata = metadata };
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(payloads));
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record PromotionDefinition
    {
        public string MetadataKey { get; init; } = string.Empty;

        public string Selector { get; init; } = string.Empty;

        public string? DefaultValue { get; init; }

        public string? JoinWith { get; init; }
    }
}
