using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Represents one fetch/parse source in a flow's source graph when creating or updating a flow.</summary>
public sealed record FlowSourceRequest
{
    /// <summary>The unique id of this source within the flow, e.g. "primary" or "sqlLookup".</summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>The fetch step that acquires this source's raw content.</summary>
    [Required]
    public required FlowStepRequest Fetch { get; init; }

    /// <summary>The parse step that converts this source's raw content into the working payload shape.</summary>
    [Required]
    public required FlowStepRequest Parse { get; init; }

    /// <summary>
    /// Ids of other sources in the same flow whose parsed batches are merged and passed to this source's fetch
    /// module as its input. Empty for root sources. The resulting graph must be acyclic.
    /// </summary>
    public IReadOnlyList<string> InputSourceIds { get; init; } = [];
}
