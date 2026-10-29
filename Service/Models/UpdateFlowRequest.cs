using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for updating a runtime-configurable flow.</summary>
public sealed record UpdateFlowRequest : IFlowPipelineRequest
{
    /// <summary>The unique flow id.</summary>
    [Required]
    public required string Id { get; init; }

    public string? Name { get; init; }

    public string? Category { get; init; }

    /// <summary>Whether the flow is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The flow trigger configuration.</summary>
    [Required]
    public required FlowTriggerRequest Trigger { get; init; }

    /// <summary>
    /// The fetch nodes of the flow's source graph, each optionally consuming the parsed output of parse nodes via
    /// <see cref="FlowFetchNodeRequest.InputParseIds"/>. Ids must be non-empty and unique across both fetch and
    /// parse nodes, and every fetch node must be parsed by at least one parse node.
    /// </summary>
    [Required]
    public required IReadOnlyList<FlowFetchNodeRequest> Fetches { get; init; }

    /// <summary>
    /// The parse nodes of the flow's source graph, each parsing the raw output of one or more fetch nodes listed in
    /// <see cref="FlowParseNodeRequest.FetchNodeIds"/>. Referenced ids must exist, and the combined fetch/parse graph
    /// must be acyclic.
    /// </summary>
    [Required]
    public required IReadOnlyList<FlowParseNodeRequest> Parses { get; init; }

    /// <summary>The orchestration augment steps executed after parsing.</summary>
    public IReadOnlyList<FlowStepRequest> Augments { get; init; } = [];

    /// <summary>The delivery routes executed after augmentation.</summary>
    public IReadOnlyList<DeliveryRouteRequest> Deliveries { get; init; } = [];

    /// <summary>Default retry policy applied to any step in this flow that doesn't define its own retry override.</summary>
    public RetryPolicyRequest? Retry { get; init; }
}
