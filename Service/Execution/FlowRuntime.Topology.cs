using Mulse.Modules;

namespace Service.Execution;

/// <summary>Whether a <see cref="SourceGraphStep"/> runs a fetch node or a parse node.</summary>
internal enum SourceGraphNodeKind
{
    Fetch,
    Parse
}

/// <summary>
/// One step of a flow's topologically ordered source graph: exactly one of <see cref="Fetch"/> /
/// <see cref="Parse"/> is non-null, matching <see cref="Kind"/>.
/// </summary>
internal readonly record struct SourceGraphStep(SourceGraphNodeKind Kind, FetchNodeDefinition? Fetch, ParseNodeDefinition? Parse)
{
    public string Id => Kind == SourceGraphNodeKind.Fetch ? Fetch!.Id : Parse!.Id;

    public static SourceGraphStep ForFetch(FetchNodeDefinition fetch) => new(SourceGraphNodeKind.Fetch, fetch, null);

    public static SourceGraphStep ForParse(ParseNodeDefinition parse) => new(SourceGraphNodeKind.Parse, null, parse);
}

public sealed partial class FlowRuntime
{
    /// <summary>
    /// Orders a flow's fetch and parse nodes together so that every node runs after the nodes it consumes
    /// (Kahn's algorithm over one combined graph): a fetch node depends on the parse nodes in its
    /// <see cref="FetchNodeDefinition.InputParseIds"/>, and a parse node depends on the fetch nodes in its
    /// <see cref="ParseNodeDefinition.FetchNodeIds"/>. Source graphs are validated when a flow is created or
    /// updated, but the runtime defends against a stale or externally edited definition by failing fast with a
    /// clear <see cref="InvalidOperationException"/>.
    /// </summary>
    private static IReadOnlyList<SourceGraphStep> OrderSourceGraphTopologically(PipelineDefinition pipeline)
        => OrderSourceGraph(pipeline, static message => new InvalidOperationException(message));

    /// <summary>
    /// Validates and topologically orders a flow's combined fetch/parse graph. Shared by the runtime and by
    /// <see cref="Flows.FlowDefinitionService"/> (which surfaces violations as 400 Bad Request), so
    /// <paramref name="createError"/> decides the exception type thrown for every violation: missing or duplicate
    /// ids (within either list or across both lists, since fetch and parse ids share one namespace), parse nodes
    /// with no fetch nodes, fetch nodes that no parse node consumes, dangling
    /// <see cref="FetchNodeDefinition.InputParseIds"/> / <see cref="ParseNodeDefinition.FetchNodeIds"/> references,
    /// and cycles.
    /// </summary>
    internal static IReadOnlyList<SourceGraphStep> OrderSourceGraph(PipelineDefinition pipeline, Func<string, Exception> createError)
    {
        var nodesById = new Dictionary<string, SourceGraphStep>(StringComparer.OrdinalIgnoreCase);
        var declarationOrder = new List<SourceGraphStep>(pipeline.Fetches.Count + pipeline.Parses.Count);

        foreach (var fetch in pipeline.Fetches)
        {
            if (string.IsNullOrWhiteSpace(fetch.Id))
            {
                throw createError($"Flow '{pipeline.Id}' has a fetch node with no id.");
            }

            if (!nodesById.TryAdd(fetch.Id, SourceGraphStep.ForFetch(fetch)))
            {
                throw createError($"Flow '{pipeline.Id}' declares more than one fetch node with id '{fetch.Id}'.");
            }

            declarationOrder.Add(nodesById[fetch.Id]);
        }

        foreach (var parse in pipeline.Parses)
        {
            if (string.IsNullOrWhiteSpace(parse.Id))
            {
                throw createError($"Flow '{pipeline.Id}' has a parse node with no id.");
            }

            if (nodesById.TryGetValue(parse.Id, out var existing))
            {
                throw createError(existing.Kind == SourceGraphNodeKind.Fetch
                    ? $"Flow '{pipeline.Id}' uses id '{parse.Id}' for both a fetch node and a parse node; fetch and parse node ids must be unique across both."
                    : $"Flow '{pipeline.Id}' declares more than one parse node with id '{parse.Id}'.");
            }

            nodesById[parse.Id] = SourceGraphStep.ForParse(parse);
            declarationOrder.Add(nodesById[parse.Id]);
        }

        var remainingDependencies = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        void AddDependencies(string nodeId, IReadOnlyList<string> dependencyIds)
        {
            foreach (var dependencyId in dependencyIds)
            {
                if (!dependents.TryGetValue(dependencyId, out var list))
                {
                    list = [];
                    dependents[dependencyId] = list;
                }

                list.Add(nodeId);
            }

            remainingDependencies[nodeId] = dependencyIds.Count;
        }

        foreach (var fetch in pipeline.Fetches)
        {
            var distinctInputs = DistinctIds(fetch.InputParseIds);
            foreach (var inputParseId in distinctInputs)
            {
                if (!nodesById.TryGetValue(inputParseId, out var input) || input.Kind != SourceGraphNodeKind.Parse)
                {
                    throw createError(
                        $"Flow '{pipeline.Id}' fetch node '{fetch.Id}' references unknown input parse node '{inputParseId}'.");
                }
            }

            AddDependencies(fetch.Id, distinctInputs);
        }

        foreach (var parse in pipeline.Parses)
        {
            var distinctFetches = DistinctIds(parse.FetchNodeIds);
            if (distinctFetches.Count == 0)
            {
                throw createError($"Flow '{pipeline.Id}' parse node '{parse.Id}' must reference at least one fetch node.");
            }

            foreach (var fetchNodeId in distinctFetches)
            {
                if (!nodesById.TryGetValue(fetchNodeId, out var input) || input.Kind != SourceGraphNodeKind.Fetch)
                {
                    throw createError(
                        $"Flow '{pipeline.Id}' parse node '{parse.Id}' references unknown fetch node '{fetchNodeId}'.");
                }
            }

            AddDependencies(parse.Id, distinctFetches);
        }

        // A fetch node no parse node consumes would still run (fetch modules may have side effects such as deleting
        // the files they read) yet its output would be silently discarded, so it is rejected rather than executed.
        foreach (var fetch in pipeline.Fetches)
        {
            if (!dependents.ContainsKey(fetch.Id))
            {
                throw createError($"Flow '{pipeline.Id}' fetch node '{fetch.Id}' is not parsed by any parse node.");
            }
        }

        var ready = new Queue<string>(declarationOrder
            .Where(node => remainingDependencies[node.Id] == 0)
            .Select(static node => node.Id));
        var ordered = new List<SourceGraphStep>(declarationOrder.Count);

        while (ready.Count > 0)
        {
            var nodeId = ready.Dequeue();
            ordered.Add(nodesById[nodeId]);

            if (!dependents.TryGetValue(nodeId, out var nodeDependents))
            {
                continue;
            }

            foreach (var dependentId in nodeDependents)
            {
                if (--remainingDependencies[dependentId] == 0)
                {
                    ready.Enqueue(dependentId);
                }
            }
        }

        if (ordered.Count != declarationOrder.Count)
        {
            var cyclicIds = declarationOrder
                .Select(static node => node.Id)
                .Where(id => remainingDependencies[id] > 0)
                .ToArray();
            throw createError(
                $"Flow '{pipeline.Id}' has a cyclic source graph involving: {string.Join(", ", cyclicIds)}.");
        }

        return ordered;
    }

    // Blank entries are kept (not filtered) so they surface as "unknown ... ''" reference errors.
    private static List<string> DistinctIds(IEnumerable<string> ids)
        => ids.Select(static id => id ?? string.Empty).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
