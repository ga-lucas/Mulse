using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class RouteSelectionAugmentModule : IOrchestrationAugmentModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("routeRulesJson", "Route rules JSON", "A JSON array of metadata-based routing rules.", true, ModuleSettingInputKind.TextArea,
            "[{\"metadataKey\":\"documentType\",\"equals\":\"Order\",\"route\":\"orders\"}]"),
        new("targetMetadataKey", "Target metadata key", "The metadata key that receives the selected route.", false, ModuleSettingInputKind.Text, "targetRoute"),
        new("defaultRoute", "Default route", "Optional default route when no rules match.", false),
        new("caseInsensitive", "Case-insensitive matching", "Matches metadata values without case sensitivity when enabled.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Decision, ModuleCapability.Enrichment]);

    public ModuleDescriptor Descriptor { get; } = new(
        "route-selection-augment",
        "Route selection augment",
        ModuleKind.OrchestrationAugment,
        "Assigns a delivery route metadata key from promoted metadata so render and deliver stages can branch cleanly.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rulesJson = ModuleSettings.GetRequired(step.Settings, "routeRulesJson", Descriptor.Id);
        var targetMetadataKey = ModuleSettings.GetOptional(step.Settings, "targetMetadataKey") ?? "targetRoute";
        var defaultRoute = ModuleSettings.GetOptional(step.Settings, "defaultRoute");
        var caseInsensitive = ModuleSettings.GetBoolean(step.Settings, "caseInsensitive", defaultValue: true, Descriptor.Id);
        var rules = JsonSerializer.Deserialize<IReadOnlyList<RouteSelectionRule>>(rulesJson, SerializerOptions)
            ?? throw new InvalidOperationException($"Module '{Descriptor.Id}' requires a non-empty routeRulesJson array.");

        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase);
            var route = rules.FirstOrDefault(rule => MatchesRule(metadata, rule, comparison))?.Route ?? defaultRoute;
            if (!string.IsNullOrWhiteSpace(route))
            {
                metadata[targetMetadataKey] = route;
                metadata["routedBy"] = Descriptor.Id;
            }

            return payload with { Metadata = metadata };
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(payloads));
    }

    private static bool MatchesRule(IReadOnlyDictionary<string, string> metadata, RouteSelectionRule rule, StringComparison comparison)
    {
        if (string.IsNullOrWhiteSpace(rule.MetadataKey) || string.IsNullOrWhiteSpace(rule.Route))
        {
            throw new InvalidOperationException("Each route selection rule requires metadataKey and route values.");
        }

        if (!metadata.TryGetValue(rule.MetadataKey, out var candidate) || string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.EqualsValue))
        {
            return string.Equals(candidate, rule.EqualsValue, comparison);
        }

        if (!string.IsNullOrWhiteSpace(rule.Contains))
        {
            return candidate.Contains(rule.Contains, comparison);
        }

        return true;
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record RouteSelectionRule
    {
        public string MetadataKey { get; init; } = string.Empty;

        [JsonPropertyName("equals")]
        public string? EqualsValue { get; init; }

        public string? Contains { get; init; }

        public string Route { get; init; } = string.Empty;
    }
}
