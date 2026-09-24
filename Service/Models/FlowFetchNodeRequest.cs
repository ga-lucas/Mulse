using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Represents one fetch node in a flow's source graph when creating or updating a flow.</summary>
public sealed record FlowFetchNodeRequest
{
    /// <summary>
    /// The unique id of this fetch node within the flow, e.g. "primaryFiles" or "sqlLookup". Must also be distinct
    /// from every parse node id in the same flow.
    /// </summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>The fetch step that acquires this node's raw content.</summary>
    [Required]
    public required FlowStepRequest Fetch { get; init; }

    /// <summary>
    /// Ids of parse nodes in the same flow whose parsed batches are merged and passed to this node's fetch module
    /// as its input. Empty for root fetch nodes. The combined fetch/parse graph must be acyclic.
    /// </summary>
    public IReadOnlyList<string> InputParseIds { get; init; } = [];
}
