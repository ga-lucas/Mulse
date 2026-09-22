using Mulse.Modules;

namespace Service.Execution;

public sealed partial class FlowRuntime
{
    /// <summary>
    /// Orders a flow's sources so that every source runs after the sources it consumes (Kahn's algorithm).
    /// Source graphs are validated when a flow is created or updated, but the runtime defends against a stale or
    /// externally edited definition by failing fast with a clear message.
    /// </summary>
    private static IReadOnlyList<SourceDefinition> OrderSourcesTopologically(PipelineDefinition pipeline)
    {
        var sourcesById = new Dictionary<string, SourceDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in pipeline.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
            {
                throw new InvalidOperationException($"Flow '{pipeline.Id}' has a source with no id.");
            }

            if (!sourcesById.TryAdd(source.Id, source))
            {
                throw new InvalidOperationException($"Flow '{pipeline.Id}' declares more than one source with id '{source.Id}'.");
            }
        }

        var remainingDependencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in pipeline.Sources)
        {
            var distinctInputs = source.InputSourceIds
                .Where(static inputId => !string.IsNullOrWhiteSpace(inputId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            foreach (var inputId in distinctInputs)
            {
                if (!sourcesById.ContainsKey(inputId))
                {
                    throw new InvalidOperationException(
                        $"Flow '{pipeline.Id}' source '{source.Id}' references unknown input source '{inputId}'.");
                }

                if (!dependents.TryGetValue(inputId, out var list))
                {
                    list = [];
                    dependents[inputId] = list;
                }

                list.Add(source.Id);
            }

            remainingDependencies[source.Id] = distinctInputs.Length;
        }

        var ready = new Queue<string>(pipeline.Sources
            .Where(source => remainingDependencies[source.Id] == 0)
            .Select(static source => source.Id));
        var ordered = new List<SourceDefinition>(pipeline.Sources.Count);

        while (ready.Count > 0)
        {
            var sourceId = ready.Dequeue();
            ordered.Add(sourcesById[sourceId]);

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

        if (ordered.Count != pipeline.Sources.Count)
        {
            var cyclicIds = pipeline.Sources
                .Select(static source => source.Id)
                .Where(id => remainingDependencies[id] > 0)
                .ToArray();
            throw new InvalidOperationException(
                $"Flow '{pipeline.Id}' has a cyclic source graph involving: {string.Join(", ", cyclicIds)}.");
        }

        return ordered;
    }
}
