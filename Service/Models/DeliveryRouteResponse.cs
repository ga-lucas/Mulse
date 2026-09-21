namespace Service.Models;

/// <summary>Represents one configured render and deliver route inside a flow.</summary>
public sealed record DeliveryRouteResponse(
    ModuleStepResponse Render,
    ModuleStepResponse Deliver);
