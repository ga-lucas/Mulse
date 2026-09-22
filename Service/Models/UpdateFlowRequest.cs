using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for updating a runtime-configurable flow.</summary>
public sealed record UpdateFlowRequest : IFlowPipelineRequest
{
    /// <summary>The unique flow id.</summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>Whether the flow is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The flow trigger configuration.</summary>
    [Required]
    public required FlowTriggerRequest Trigger { get; init; }

    /// <summary>
    /// The flow's source graph: each entry is a fetch/parse pair, optionally consuming other sources' parsed
    /// output. Ids must be unique and non-empty, input source ids must reference sources in this same list, and
    /// the resulting graph must be acyclic.
    /// </summary>
    [Required]
    public required IReadOnlyList<FlowSourceRequest> Sources { get; init; }

    /// <summary>The orchestration augment steps executed after parsing.</summary>
    public IReadOnlyList<FlowStepRequest> Augments { get; init; } = [];

    /// <summary>The delivery routes executed after augmentation.</summary>
    public IReadOnlyList<DeliveryRouteRequest> Deliveries { get; init; } = [];

    /// <summary>Default retry policy applied to any step in this flow that doesn't define its own retry override.</summary>
    public RetryPolicyRequest? Retry { get; init; }
}
