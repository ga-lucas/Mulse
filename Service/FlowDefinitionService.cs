using Mulse.Modules;

namespace Service;

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

        if (string.IsNullOrWhiteSpace(pipeline.Fetch.Module))
        {
            throw new ArgumentException($"Flow '{pipeline.Id}' must define a fetch module.", nameof(pipeline));
        }

        if (string.IsNullOrWhiteSpace(pipeline.Parse.Module))
        {
            throw new ArgumentException($"Flow '{pipeline.Id}' must define a parse module.", nameof(pipeline));
        }

        if (pipeline.Deliveries.Count == 0)
        {
            throw new ArgumentException($"Flow '{pipeline.Id}' must define at least one delivery route.", nameof(pipeline));
        }

        var availableModules = moduleCatalog.GetAll().Select(static entry => entry.Descriptor).ToArray();

        ValidateModuleReference(pipeline.Fetch.Module, ModuleKind.Fetch, availableModules, pipeline.Id);
        ValidateModuleReference(pipeline.Parse.Module, ModuleKind.Parse, availableModules, pipeline.Id);
        ValidateConditions(pipeline.Parse, pipeline.Id);

        foreach (var augment in pipeline.Augments)
        {
            ValidateModuleReference(augment.Module, ModuleKind.OrchestrationAugment, availableModules, pipeline.Id);
            ValidateConditions(augment, pipeline.Id);
        }

        foreach (var delivery in pipeline.Deliveries)
        {
            ValidateModuleReference(delivery.Render.Module, ModuleKind.Render, availableModules, pipeline.Id);
            ValidateModuleReference(delivery.Deliver.Module, ModuleKind.Deliver, availableModules, pipeline.Id);
            ValidateConditions(delivery.Render, pipeline.Id);
            ValidateConditions(delivery.Deliver, pipeline.Id);
        }
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
