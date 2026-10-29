using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Modules;
using Mulse.Modules.Workflow;

namespace Service.Flows;

internal static class FlowModuleDependencies
{
    private static readonly JsonSerializerOptions MappingOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static IEnumerable<(string Id, ModuleKind Kind)> References(PipelineDefinition flow, IReadOnlyDictionary<string, string>? configValues = null)
    {
        foreach (var node in flow.Fetches) yield return (node.Fetch.Module, ModuleKind.Fetch);
        foreach (var node in flow.Parses) yield return (node.Parse.Module, ModuleKind.Parse);
        foreach (var step in flow.Augments)
        {
            yield return (step.Module, ModuleKind.OrchestrationAugment);
            if (string.Equals(step.Module, "multi-source-join-map-augment", StringComparison.OrdinalIgnoreCase)
                && step.Settings.TryGetValue("mappingJson", out var json) && !string.IsNullOrWhiteSpace(json))
            {
                json = ConfigValueResolver.Substitute(json, configValues, reference =>
                    throw new ArgumentException($"Flow '{flow.Id}' requires configuration '{reference}' to determine its mapping module dependencies."));
                foreach (var mapping in ParseMappings(flow.Id, json).Where(mapping => mapping.SourceKind == WorkflowFieldSourceKind.Transform))
                    yield return (mapping.TransformModule?.Trim() ?? string.Empty, ModuleKind.Transform);
            }
        }
        foreach (var route in flow.Deliveries)
        {
            yield return (route.Render.Module, ModuleKind.Render);
            foreach (var action in route.Delivers)
            {
                yield return (action.Deliver.Module, ModuleKind.Deliver);
                if (action.Compensation is { } compensation) yield return (compensation.Module, ModuleKind.Deliver);
            }
        }
    }

    public static IReadOnlyList<string> FindMissing(PipelineDefinition flow, IReadOnlyList<ModuleCatalogEntry> available, IReadOnlyDictionary<string, string>? configValues = null)
    {
        try
        {
            return References(flow, configValues).Where(reference => !available.Any(entry => entry.Descriptor.Kind == reference.Kind
                    && string.Equals(entry.Descriptor.Id, reference.Id, StringComparison.OrdinalIgnoreCase)))
                .Select(reference => $"{reference.Kind}: {reference.Id}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (ArgumentException exception)
        {
            return [exception.Message];
        }
    }

    public static void EnsureAvailable(PipelineDefinition flow, IReadOnlyList<ModuleCatalogEntry> available, IReadOnlyDictionary<string, string>? configValues = null)
    {
        var missing = FindMissing(flow, available, configValues);
        if (missing.Count > 0)
            throw new InvalidOperationException($"Flow '{flow.Id}' has unknown or unloaded module dependencies: {string.Join(", ", missing)}. Load the required packages or correct the module references before enabling or running it.");
    }

    private static WorkflowFieldMappingDefinition[] ParseMappings(string flowId, string json)
    {
        try
        {
            var mappings = JsonSerializer.Deserialize<WorkflowFieldMappingDefinition[]>(json, MappingOptions)
                ?? throw new ArgumentException($"Flow '{flowId}' has an empty mappingJson document.");
            if (mappings.Any(mapping => mapping is null))
                throw new ArgumentException($"Flow '{flowId}' has a null mappingJson entry; module dependencies cannot be determined.");
            return mappings;
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Flow '{flowId}' has invalid mappingJson; module dependencies cannot be determined.", exception);
        }
    }
}
