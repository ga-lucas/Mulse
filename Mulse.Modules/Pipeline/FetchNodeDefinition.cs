namespace Mulse.Modules.Pipeline;

/// <summary>
/// One fetch node inside a flow's source graph. A fetch node acquires raw (unparsed) content; one or more
/// <see cref="ParseNodeDefinition"/>s list it in <see cref="ParseNodeDefinition.FetchNodeIds"/> to normalize that
/// raw content. A fetch node can itself consume the already-parsed output of other parse nodes through
/// <see cref="InputParseIds"/> (for example a SQL lookup fetch keyed off a primary JSON parse node).
/// </summary>
public sealed class FetchNodeDefinition
{
    /// <summary>
    /// Unique id of this fetch node within the flow, e.g. "primaryFiles" or "sqlLookup". Must also be distinct from
    /// every <see cref="ParseNodeDefinition.Id"/> in the same flow.
    /// </summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The fetch step that acquires this node's raw content.</summary>
    public ModuleStepDefinition Fetch { get; init; } = new();

    /// <summary>
    /// Ids of parse nodes in the same flow whose PARSED batches are merged and handed to this node's fetch module
    /// as its input batch (each payload carries a <c>sourceId</c> metadata entry naming the parse node that produced
    /// it). Empty for root fetch nodes (the fetch module receives <see cref="IntegrationBatch.Empty"/>). The combined
    /// fetch/parse graph must be acyclic.
    /// </summary>
    public List<string> InputParseIds { get; init; } = [];
}
