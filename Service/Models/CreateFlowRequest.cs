using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for creating a runtime-configurable flow.</summary>
public sealed record CreateFlowRequest
{
    /// <summary>The unique flow id.</summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>Whether the flow is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The flow trigger configuration.</summary>
    [Required]
    public required FlowTriggerRequest Trigger { get; init; }

    /// <summary>The fetch step that acquires raw source content.</summary>
    [Required]
    public required FlowStepRequest Fetch { get; init; }

    /// <summary>The parse step that converts raw content into the working payload shape.</summary>
    [Required]
    public required FlowStepRequest Parse { get; init; }

    /// <summary>The orchestration augment steps executed after parsing.</summary>
    public IReadOnlyList<FlowStepRequest> Augments { get; init; } = [];

    /// <summary>The delivery routes executed after augmentation.</summary>
    public IReadOnlyList<DeliveryRouteRequest> Deliveries { get; init; } = [];
}
