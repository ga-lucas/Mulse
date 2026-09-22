namespace Mulse.Modules.Pipeline;

/// <summary>
/// One fetch/parse pair inside a flow's source graph. A flow resolves every source it declares before any
/// augment runs; sources are ordered topologically by <see cref="InputSourceIds"/> so a source can consume the
/// already-parsed output of other sources (for example a SQL lookup source keyed off a primary JSON source).
/// </summary>
public sealed class SourceDefinition
{
    /// <summary>Unique id of this source within the flow, e.g. "primary" or "sqlLookup".</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The fetch step that acquires this source's raw content.</summary>
    public ModuleStepDefinition Fetch { get; init; } = new();

    /// <summary>The parse step that normalizes this source's raw content.</summary>
    public ModuleStepDefinition Parse { get; init; } = new();

    /// <summary>
    /// Ids of other sources in the same flow whose PARSED batches are merged and handed to this source's fetch
    /// module as its input batch. Empty for root sources (the fetch module receives
    /// <see cref="IntegrationBatch.Empty"/>). The graph must be acyclic.
    /// </summary>
    public List<string> InputSourceIds { get; init; } = [];
}
