namespace Service.Models;

/// <summary>Represents one configured delivery route inside a flow: a single render step and its deliver actions.</summary>
public sealed record DeliveryRouteResponse(
    ModuleStepResponse Render,
    IReadOnlyList<DeliveryActionResponse> Delivers,
    string? AtomicScope = null);