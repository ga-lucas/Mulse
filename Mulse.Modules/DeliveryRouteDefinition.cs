namespace Mulse.Modules;

public sealed class DeliveryRouteDefinition
{
    public ModuleStepDefinition Render { get; init; } = new();

    public ModuleStepDefinition Deliver { get; init; } = new();
}
