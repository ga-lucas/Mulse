using Mulse.Modules;

namespace Service.Flows;

public sealed class FlowDefinitionService(IRuntimeConfigurationStore runtimeConfigurationStore, IModuleCatalog moduleCatalog) : IFlowDefinitionService
{
    private const string ConditionJsonSetting = "conditionJson";

    public IReadOnlyList<PipelineDefinition> GetAll()
    {
        return runtimeConfigurationStore.GetState().Pipelines
            .OrderBy(static flow => flow.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public PipelineDefinition GetById(string flowId)
    {
        return GetAll().FirstOrDefault(flow => string.Equals(flow.Id, flowId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No configured flow with id '{flowId}' was found.");
    }

    public async Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken)
    {
        ValidatePipeline(pipeline, allowExisting: false);
        await runtimeConfigurationStore.UpsertFlowAsync(pipeline, cancellationToken).ConfigureAwait(false);
        return GetById(pipeline.Id);
    }

    public async Task<PipelineDefinition> UpdateAsync(string flowId, PipelineDefinition pipeline, CancellationToken cancellationToken)
    {
        if (!string.Equals(flowId, pipeline.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The flow id in the route must match the payload.", nameof(flowId));
        }

        _ = GetById(flowId);
        ValidatePipeline(pipeline, allowExisting: true);
        await runtimeConfigurationStore.UpsertFlowAsync(pipeline, cancellationToken).ConfigureAwait(false);
        return GetById(flowId);
    }

    public async Task DeleteAsync(string flowId, CancellationToken cancellationToken)
    {
        _ = GetById(flowId);
        await runtimeConfigurationStore.DeleteFlowAsync(flowId, cancellationToken).ConfigureAwait(false);
    }

    private void ValidatePipeline(PipelineDefinition pipeline, bool allowExisting)
    {
        if (string.IsNullOrWhiteSpace(pipeline.Id))
        {
            throw new ArgumentException("Flow id is required.", nameof(pipeline));
        }

        var existingFlow = GetAll().FirstOrDefault(flow => string.Equals(flow.Id, pipeline.Id, StringComparison.OrdinalIgnoreCase));
        if (!allowExisting && existingFlow is not null)
        {
            throw new InvalidOperationException($"Flow '{pipeline.Id}' already exists.");
        }

        if (pipeline.Fetches.Count == 0 || pipeline.Parses.Count == 0)
        {
            throw new ArgumentException($"Flow '{pipeline.Id}' must define at least one fetch node and one parse node.", nameof(pipeline));
        }

        if (pipeline.Deliveries.Count == 0)
        {
            throw new ArgumentException($"Flow '{pipeline.Id}' must define at least one delivery route.", nameof(pipeline));
        }

        var availableModules = moduleCatalog.GetAll().Select(static entry => entry.Descriptor).ToArray();

        ValidateSourceGraph(pipeline);

        foreach (var fetch in pipeline.Fetches)
        {
            ValidateModuleReference(fetch.Fetch.Module, ModuleKind.Fetch, availableModules, pipeline.Id);
        }

        foreach (var parse in pipeline.Parses)
        {
            ValidateModuleReference(parse.Parse.Module, ModuleKind.Parse, availableModules, pipeline.Id);
            ValidateConditions(parse.Parse, pipeline.Id);
        }

        foreach (var augment in pipeline.Augments)
        {
            ValidateModuleReference(augment.Module, ModuleKind.OrchestrationAugment, availableModules, pipeline.Id);
            ValidateConditions(augment, pipeline.Id);
        }

        for (var routeIndex = 0; routeIndex < pipeline.Deliveries.Count; routeIndex++)
        {
            var delivery = pipeline.Deliveries[routeIndex];
            if (delivery.Delivers.Count == 0)
            {
                throw new ArgumentException($"Flow '{pipeline.Id}' delivery route {routeIndex} must define at least one deliver action.", nameof(pipeline));
            }

            ValidateModuleReference(delivery.Render.Module, ModuleKind.Render, availableModules, pipeline.Id);
            ValidateConditions(delivery.Render, pipeline.Id);
            foreach (var action in delivery.Delivers)
            {
                ValidateModuleReference(action.Deliver.Module, ModuleKind.Deliver, availableModules, pipeline.Id);
                ValidateConditions(action.Deliver, pipeline.Id);
            }
        }
    }

    /// <summary>
    /// Verifies that a flow's source graph is usable: every fetch/parse node names a module, and the combined
    /// fetch/parse graph passes the same structural rules the runtime enforces (see
    /// <see cref="FlowRuntime.OrderSourceGraph"/>): ids present and unique across both node lists, parse nodes list
    /// at least one fetch node, every fetch node is parsed, references resolve, and the graph is acyclic. Failures
    /// are raised as <see cref="ArgumentException"/> so the API surfaces them as 400 Bad Request (see
    /// ApiExceptionHandler).
    /// </summary>
    private static void ValidateSourceGraph(PipelineDefinition pipeline)
    {
        foreach (var fetch in pipeline.Fetches)
        {
            if (string.IsNullOrWhiteSpace(fetch.Fetch.Module))
            {
                throw new ArgumentException($"Flow '{pipeline.Id}' fetch node '{fetch.Id}' must define a fetch module.", nameof(pipeline));
            }
        }

        foreach (var parse in pipeline.Parses)
        {
            if (string.IsNullOrWhiteSpace(parse.Parse.Module))
            {
                throw new ArgumentException($"Flow '{pipeline.Id}' parse node '{parse.Id}' must define a parse module.", nameof(pipeline));
            }
        }

        _ = FlowRuntime.OrderSourceGraph(pipeline, message => new ArgumentException(message, nameof(pipeline)));
    }

    private static void ValidateModuleReference(string moduleId, ModuleKind kind, IReadOnlyCollection<ModuleDescriptor> descriptors, string flowId)
    {
        if (!descriptors.Any(descriptor => descriptor.Kind == kind && string.Equals(descriptor.Id, moduleId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Flow '{flowId}' references unknown {kind} module '{moduleId}'.");
        }
    }

    private static void ValidateConditions(ModuleStepDefinition step, string flowId)
    {
        if (step.Settings.TryGetValue(ConditionJsonSetting, out var conditionJson) && !string.IsNullOrWhiteSpace(conditionJson))
        {
            _ = DecisionRuntime.ParseConditions(conditionJson, $"{flowId}:{step.Module}");
        }
    }
}
