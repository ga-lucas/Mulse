using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Modules;
using Mulse.Modules.Mapping;
using Mulse.Modules.Orchestration;
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
            if (step.Module.Equals(Mulse.Modules.ControlFlow.NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase))
            {
                var nativeJson = ConfigValueResolver.SubstituteJson(step.Settings.GetValueOrDefault("flowJson", ""), configValues, reference =>
                    throw new ArgumentException($"Flow '{flow.Id}' requires configuration '{reference}' for native control-flow dependencies."), dependenciesOnly: true, sizeLimit: 1_000_000);
                var native = Mulse.Modules.ControlFlow.NativeFlowDefinition.Read(nativeJson, allowBlocked: !flow.Enabled);
                foreach (var node in native.AllNodes())
                {
                    if (node.Kind == Mulse.Modules.ControlFlow.NativeFlowNodeKind.Deliver)
                    {
                        yield return (node.Module, ModuleKind.Deliver);
                        continue;
                    }
                    var nested = new ModuleStepDefinition { Module = node.Module, Settings = node.Settings };
                    var child = node.Kind == Mulse.Modules.ControlFlow.NativeFlowNodeKind.Augment
                        ? new PipelineDefinition { Id = flow.Id, Enabled = flow.Enabled, Augments = [nested] }
                        : node.Kind == Mulse.Modules.ControlFlow.NativeFlowNodeKind.Render
                            ? new PipelineDefinition { Id = flow.Id, Enabled = flow.Enabled, Deliveries = [new() { Render = nested }] } : null;
                    if (child is not null)
                        foreach (var reference in References(child, configValues)) yield return reference;
                }
            }
            foreach (var choice in DispatchChoices(flow.Id, step, configValues))
            {
                yield return (choice.Module, ModuleKind.Render);
                foreach (var stage in PipelineStages(flow.Id, choice.Module, choice.Settings, configValues, resolveReferences: false)) yield return (stage.Module, ModuleKind.Render);
            }
            if (string.Equals(step.Module, Mulse.Modules.BuiltIn.Augment.OdxProgramAugmentModule.ModuleId, StringComparison.OrdinalIgnoreCase))
            {
                var sendsJson = ConfigValueResolver.SubstituteJson(step.Settings.GetValueOrDefault("sendsJson", "[]"), configValues, reference =>
                    throw new ArgumentException($"Flow '{flow.Id}' requires configuration '{reference}' to determine its outbound ODX dependencies."), dependenciesOnly: true);
                foreach (var send in OdxSendBindings.Read(sendsJson))
                {
                    yield return (send.Module, ModuleKind.Deliver);
                    foreach (var stage in send.Pipeline) yield return (stage.Module, ModuleKind.Render);
                }
            }
            var isJoin = string.Equals(step.Module, "multi-source-join-map-augment", StringComparison.OrdinalIgnoreCase);
            var isParsedMap = string.Equals(step.Module, "parsed-data-map-augment", StringComparison.OrdinalIgnoreCase);
            if ((isJoin || isParsedMap) && step.Settings.TryGetValue("mappingJson", out var json) && !string.IsNullOrWhiteSpace(json))
            {
                json = ConfigValueResolver.Substitute(json, configValues, reference =>
                    throw new ArgumentException($"Flow '{flow.Id}' requires configuration '{reference}' to determine its mapping module dependencies."));
                if (isParsedMap)
                {
                    foreach (var module in ParsedDataMapping.TransformModules(ParsedDataMapping.ReadMap(json)))
                        yield return (module, ModuleKind.Transform);
                }
                else
                {
                    foreach (var mapping in ParseMappings(flow.Id, json).Where(mapping => mapping.SourceKind == WorkflowFieldSourceKind.Transform))
                        yield return (mapping.TransformModule?.Trim() ?? string.Empty, ModuleKind.Transform);
                }
            }
        }
        foreach (var route in flow.Deliveries)
        {
            yield return (route.Render.Module, ModuleKind.Render);
            foreach (var stage in PipelineStages(flow.Id, route.Render.Module, route.Render.Settings, configValues)) yield return (stage.Module, ModuleKind.Render);
            foreach (var choice in DispatchChoices(flow.Id, route.Render, configValues))
            {
                yield return (choice.Module, ModuleKind.Render);
                foreach (var stage in PipelineStages(flow.Id, choice.Module, choice.Settings, configValues, resolveReferences: false)) yield return (stage.Module, ModuleKind.Render);
            }
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
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
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

    private static IEnumerable<RenderDispatchCase> DispatchChoices(string flowId, ModuleStepDefinition step, IReadOnlyDictionary<string, string>? configValues)
    {
        var isOdx = step.Module.Equals(Mulse.Modules.BuiltIn.Augment.OdxProgramAugmentModule.ModuleId, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(step.Module, RenderDispatchDefinition.ModuleId, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(step.Module, RenderDispatchDefinition.AugmentModuleId, StringComparison.OrdinalIgnoreCase) && !isOdx) yield break;
        var json = ConfigValueResolver.SubstituteJson(step.Settings.GetValueOrDefault("casesJson", isOdx ? "[]" : ""), configValues, reference =>
            throw new ArgumentException($"Flow '{flowId}' requires configuration '{reference}' to determine its render dispatch dependencies."), dependenciesOnly: true);
        foreach (var choice in RenderDispatchDefinition.Read(json, allowEmpty: isOdx)) yield return choice;
    }

    private static IEnumerable<RenderPipelineStage> PipelineStages(string flowId, string module, IReadOnlyDictionary<string, string> settings, IReadOnlyDictionary<string, string>? configValues, bool resolveReferences = true)
    {
        if (!module.Equals(RenderPipelineDefinition.ModuleId, StringComparison.OrdinalIgnoreCase)) return [];
        var json = settings.GetValueOrDefault("stagesJson", "[]");
        if (resolveReferences) json = ConfigValueResolver.SubstituteJson(json, configValues, reference =>
            throw new ArgumentException($"Flow '{flowId}' requires configuration '{reference}' to determine its renderer pipeline dependencies."), dependenciesOnly: true);
        return RenderPipelineDefinition.Read(json);
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
