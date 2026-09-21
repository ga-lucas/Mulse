using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for creating a runtime flow from the WYSIWYG designer.</summary>
public sealed record CreateDesignedFlowRequest
{
    /// <summary>The unique flow id.</summary>
    [Required]
    public required string Id { get; init; }

    /// <summary>Whether the flow is enabled.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The flow trigger configuration.</summary>
    [Required]
    public required FlowTriggerRequest Trigger { get; init; }

    /// <summary>The configured fetch step.</summary>
    [Required]
    public required FlowStepRequest Fetch { get; init; }

    /// <summary>The configured parse step.</summary>
    [Required]
    public required FlowStepRequest Parse { get; init; }

    /// <summary>The configured orchestration augment steps.</summary>
    public IReadOnlyList<FlowStepRequest> Augments { get; init; } = [];

    /// <summary>The configured delivery routes.</summary>
    public IReadOnlyList<DeliveryRouteRequest> Deliveries { get; init; } = [];

    /// <summary>The visual field mappings captured in the designer.</summary>
    public IReadOnlyList<FlowDesignFieldMappingRequest> Mappings { get; init; } = [];
}
