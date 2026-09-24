using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Represents one parse node in a flow's source graph when creating or updating a flow.</summary>
public sealed record FlowParseNodeRequest
{
    /// <summary>
    /// The unique id of this parse node within the flow, e.g. "primary". Must also be distinct from every fetch node
    /// id in the same flow. Parsed payloads are tagged with this id under the <c>sourceId</c> metadata key.
    /// </summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>The parse step that converts each input fetch node's raw content into the working payload shape.</summary>
    [Required]
    public required FlowStepRequest Parse { get; init; }

    /// <summary>
    /// Ids of the fetch nodes (at least one) whose raw batches are each parsed with <see cref="Parse"/> and merged into
    /// this node's single output.
    /// </summary>
    [Required]
    public required IReadOnlyList<string> FetchNodeIds { get; init; }
}
