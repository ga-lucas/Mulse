using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Represents one deliver action of a delivery route when creating or updating a flow.</summary>
public sealed record DeliveryActionRequest
{
    /// <summary>The delivery step that dispatches the route's rendered payload.</summary>
    [Required]
    public required FlowStepRequest Deliver { get; init; }

    /// <summary>Optional deliver-module step that undoes this specific delivery if another deliver action in the same atomic scope fails.</summary>
    public FlowStepRequest? Compensation { get; init; }
}