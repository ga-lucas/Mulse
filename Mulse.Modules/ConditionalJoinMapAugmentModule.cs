using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Mulse.Modules;

public sealed class ConditionalJoinMapAugmentModule : IOrchestrationAugmentModule
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        capabilities: [ModuleCapability.Mapping, ModuleCapability.Transformation]);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("joinMode", "Join mode", "Controls whether payloads without lookup matches still continue through the flow.", false, ModuleSettingInputKind.Select, WorkflowJoinMode.LeftJoin.ToString(),
        [
            new ModuleSettingOption(WorkflowJoinMode.LeftJoin.ToString(), "Left join"),
            new ModuleSettingOption(WorkflowJoinMode.InnerJoin.ToString(), "Inner join")
        ]),
        new("mappingJson", "Field mappings", "A JSON array of visual field mappings that builds the final working document.", true, ModuleSettingInputKind.TextArea, "[]")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "conditional-join-map-augment",
        "Conditional join and map augment",
        ModuleKind.OrchestrationAugment,
        "Combines the source payload and SQL lookup results into a mapped JSON document using configurable join behavior and field mappings.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var joinMode = ParseJoinMode(ModuleSettingReader.GetOptional(step.Settings, "joinMode"));
        var mappings = ParseMappings(ModuleSettingReader.GetRequired(step.Settings, "mappingJson", Descriptor.Id));
        var transformedPayloads = new List<IntegrationPayload>(batch.Payloads.Count);

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var workingNode = JsonPayloadNavigator.Parse(payload.Content, Descriptor.Id, payload.Name);
            var sourceNode = workingNode["source"]?.DeepClone() ?? workingNode.DeepClone();
            var lookupRows = workingNode["lookup"]?["rows"] as JsonArray;
            var lookupNode = lookupRows?.FirstOrDefault()?.DeepClone();
            var hasLookup = lookupRows is { Count: > 0 };

            if (joinMode == WorkflowJoinMode.InnerJoin && !hasLookup)
            {
                continue;
            }

            var mappedDocument = new JsonObject();
            foreach (var mapping in mappings)
            {
                if (!ShouldApply(mapping.Condition, hasLookup))
                {
                    continue;
                }

                var value = ResolveValue(mapping, sourceNode, lookupNode);
                if (value is null)
                {
                    continue;
                }

                JsonPayloadNavigator.SetNode(mappedDocument, mapping.TargetField, value);
            }

            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["joinMode"] = joinMode.ToString(),
                ["lookupMatched"] = hasLookup.ToString()
            };

            transformedPayloads.Add(new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".json"),
                BinaryData.FromString(mappedDocument.ToJsonString(SerializerOptions)),
                "application/json",
                metadata));
        }

        return Task.FromResult(new IntegrationBatch(transformedPayloads));
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static WorkflowJoinMode ParseJoinMode(string? joinMode)
    {
        return Enum.TryParse<WorkflowJoinMode>(joinMode, ignoreCase: true, out var parsed)
            ? parsed
            : WorkflowJoinMode.LeftJoin;
    }

    private static IReadOnlyList<WorkflowFieldMappingDefinition> ParseMappings(string mappingJson)
    {
        return JsonSerializer.Deserialize<WorkflowFieldMappingDefinition[]>(mappingJson, SerializerOptions)
            ?? [];
    }

    private static bool ShouldApply(WorkflowFieldCondition condition, bool hasLookup)
    {
        return condition switch
        {
            WorkflowFieldCondition.Always => true,
            WorkflowFieldCondition.WhenLookupFound => hasLookup,
            WorkflowFieldCondition.WhenLookupMissing => !hasLookup,
            _ => true
        };
    }

    private static JsonNode? ResolveValue(WorkflowFieldMappingDefinition mapping, JsonNode? sourceNode, JsonNode? lookupNode)
    {
        return mapping.SourceKind switch
        {
            WorkflowFieldSourceKind.Source => JsonPayloadNavigator.ReadFirstNode(sourceNode, mapping.SourcePath),
            WorkflowFieldSourceKind.Lookup => JsonPayloadNavigator.ReadFirstNode(lookupNode, mapping.SourcePath),
            WorkflowFieldSourceKind.Literal => CreateLiteralNode(mapping.LiteralValue),
            _ => null
        };
    }

    private static JsonNode? CreateLiteralNode(string? literalValue)
    {
        if (literalValue is null)
        {
            return null;
        }

        var trimmed = literalValue.Trim();
        if (trimmed.Length == 0)
        {
            return JsonValue.Create(string.Empty);
        }

        try
        {
            return JsonNode.Parse(trimmed);
        }
        catch (JsonException)
        {
            return JsonValue.Create(literalValue);
        }
    }
}
