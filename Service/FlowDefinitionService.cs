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

        if (pipeline.Sources.Count == 0)
        {
            throw new ArgumentException($"Flow '{pipeline.Id}' must define at least one source.", nameof(pipeline));
        }

        if (pipeline.Deliveries.Count == 0)
        {
            throw new ArgumentException($"Flow '{pipeline.Id}' must define at least one delivery route.", nameof(pipeline));
        }

        var availableModules = moduleCatalog.GetAll().Select(static entry => entry.Descriptor).ToArray();

        ValidateSourceGraph(pipeline);

        foreach (var source in pipeline.Sources)
        {
            ValidateModuleReference(source.Fetch.Module, ModuleKind.Fetch, availableModules, pipeline.Id);
            ValidateModuleReference(source.Parse.Module, ModuleKind.Parse, availableModules, pipeline.Id);
            ValidateConditions(source.Parse, pipeline.Id);
        }

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

    /// <summary>
    /// Verifies that a flow's source graph is usable: ids are present and unique, every fetch/parse step names a
    /// module, every declared input source exists, and the dependency graph is acyclic. Failures are raised as
    /// <see cref="ArgumentException"/> so the API surfaces them as 400 Bad Request (see ApiExceptionHandler).
    /// </summary>
    private static void ValidateSourceGraph(PipelineDefinition pipeline)
    {
        var sourceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in pipeline.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
            {
                throw new ArgumentException($"Flow '{pipeline.Id}' has a source with an empty id.", nameof(pipeline));
            }

            if (!sourceIds.Add(source.Id))
            {
                throw new ArgumentException($"Flow '{pipeline.Id}' declares more than one source with id '{source.Id}'.", nameof(pipeline));
            }

            if (string.IsNullOrWhiteSpace(source.Fetch.Module))
            {
                throw new ArgumentException($"Flow '{pipeline.Id}' source '{source.Id}' must define a fetch module.", nameof(pipeline));
            }

            if (string.IsNullOrWhiteSpace(source.Parse.Module))
            {
                throw new ArgumentException($"Flow '{pipeline.Id}' source '{source.Id}' must define a parse module.", nameof(pipeline));
            }
        }

        foreach (var source in pipeline.Sources)
        {
            foreach (var inputSourceId in source.InputSourceIds)
            {
                if (string.IsNullOrWhiteSpace(inputSourceId) || !sourceIds.Contains(inputSourceId))
                {
                    throw new ArgumentException(
                        $"Flow '{pipeline.Id}' source '{source.Id}' references unknown input source '{inputSourceId}'.",
                        nameof(pipeline));
                }

                if (string.Equals(inputSourceId, source.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"Flow '{pipeline.Id}' source '{source.Id}' cannot use itself as an input source.",
                        nameof(pipeline));
                }
            }
        }

        ValidateAcyclicSourceGraph(pipeline);
    }

    /// <summary>Kahn's algorithm: if any source never reaches zero remaining dependencies, the graph has a cycle.</summary>
    private static void ValidateAcyclicSourceGraph(PipelineDefinition pipeline)
    {
        var remainingDependencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in pipeline.Sources)
        {
            var distinctInputs = source.InputSourceIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            remainingDependencies[source.Id] = distinctInputs.Length;

            foreach (var inputSourceId in distinctInputs)
            {
                if (!dependents.TryGetValue(inputSourceId, out var list))
                {
                    list = [];
                    dependents[inputSourceId] = list;
                }

                list.Add(source.Id);
            }
        }

        var ready = new Queue<string>(remainingDependencies.Where(static entry => entry.Value == 0).Select(static entry => entry.Key));
        var resolvedCount = 0;

        while (ready.Count > 0)
        {
            var sourceId = ready.Dequeue();
            resolvedCount++;

            if (!dependents.TryGetValue(sourceId, out var sourceDependents))
            {
                continue;
            }

            foreach (var dependentId in sourceDependents)
            {
                if (--remainingDependencies[dependentId] == 0)
                {
                    ready.Enqueue(dependentId);
                }
            }
        }

        if (resolvedCount != pipeline.Sources.Count)
        {
            var cyclicIds = remainingDependencies
                .Where(static entry => entry.Value > 0)
                .Select(static entry => entry.Key)
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            throw new ArgumentException(
                $"Flow '{pipeline.Id}' has a cyclic source graph involving: {string.Join(", ", cyclicIds)}.",
                nameof(pipeline));
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
