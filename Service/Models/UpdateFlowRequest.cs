using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for updating a runtime-configurable flow.</summary>
public sealed record UpdateFlowRequest
{
    /// <summary>The unique flow id.</summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>Whether the flow is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The flow trigger configuration.</summary>
    [Required]
    public required FlowTriggerRequest Trigger { get; init; }

    /// <summary>The input step that starts the flow.</summary>
    [Required]
    public required FlowStepRequest Input { get; init; }

    /// <summary>The orchestration augment steps executed after the input step.</summary>
    public IReadOnlyList<FlowStepRequest> Augments { get; init; } = [];

    /// <summary>The output steps executed at the end of the flow.</summary>
    public IReadOnlyList<FlowStepRequest> Outputs { get; init; } = [];
}
