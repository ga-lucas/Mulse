using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Represents one delivery route within a flow: a single render step whose output is handed to one or more deliver actions.</summary>
public sealed record DeliveryRouteRequest
{
    /// <summary>The render step that prepares outbound content, run once per route.</summary>
    [Required]
    public required FlowStepRequest Render { get; init; }

    /// <summary>
    /// The deliver actions (at least one) that each independently dispatch the route's rendered payload. A permanent
    /// failure of one deliver action does not stop the others unless the route has an <see cref="AtomicScope"/>.
    /// </summary>
    [Required]
    public required IReadOnlyList<DeliveryActionRequest> Delivers { get; init; }

    /// <summary>Optional atomic-transaction scope name, applying to every deliver action of this route. Deliver actions sharing a scope are compensated together on failure.</summary>
    public string? AtomicScope { get; init; }
}