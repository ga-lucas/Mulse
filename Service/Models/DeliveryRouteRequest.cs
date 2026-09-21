using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Represents one render and deliver route within a flow.</summary>
public sealed record DeliveryRouteRequest
{
    /// <summary>The render step that prepares outbound content.</summary>
    [Required]
    public required FlowStepRequest Render { get; init; }

    /// <summary>The delivery step that dispatches the rendered payload.</summary>
    [Required]
    public required FlowStepRequest Deliver { get; init; }
}
