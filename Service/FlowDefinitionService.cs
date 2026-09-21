using Mulse.Modules;

namespace Service;

public sealed class FlowDefinitionService(IRuntimeConfigurationStore runtimeConfigurationStore, IModuleCatalog moduleCatalog) : IFlowDefinitionService
{
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

        if (string.IsNullOrWhiteSpace(pipeline.Input.Module))
        {
            throw new ArgumentException($"Flow '{pipeline.Id}' must define an input module.", nameof(pipeline));
        }

        var availableModules = moduleCatalog.GetAll().Select(static entry => entry.Descriptor).ToArray();

        ValidateModuleReference(pipeline.Input.Module, ModuleKind.Input, availableModules, pipeline.Id);
        foreach (var augment in pipeline.Augments)
        {
            ValidateModuleReference(augment.Module, ModuleKind.OrchestrationAugment, availableModules, pipeline.Id);
        }

        foreach (var output in pipeline.Outputs)
        {
            ValidateModuleReference(output.Module, ModuleKind.Output, availableModules, pipeline.Id);
        }
    }

    private static void ValidateModuleReference(string moduleId, ModuleKind kind, IReadOnlyCollection<ModuleDescriptor> descriptors, string flowId)
    {
        if (!descriptors.Any(descriptor => descriptor.Kind == kind && string.Equals(descriptor.Id, moduleId, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"Flow '{flowId}' references unknown {kind} module '{moduleId}'.");
        }
    }
}
