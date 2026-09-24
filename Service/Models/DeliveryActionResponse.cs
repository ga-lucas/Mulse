namespace Service.Models;

/// <summary>Represents one configured deliver action of a delivery route, with its optional compensation step.</summary>
public sealed record DeliveryActionResponse(
    ModuleStepResponse Deliver,
    ModuleStepResponse? Compensation = null);