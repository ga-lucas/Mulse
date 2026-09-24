namespace Mulse.Modules.Pipeline;

/// <summary>
/// One deliver action inside a <see cref="DeliveryRouteDefinition"/>. Every deliver action of a route receives the
/// same output of the route's single render step (filtered by this action's own conditions) and runs independently:
/// each has its own retry policy, resume state, and optional compensation.
/// </summary>
public sealed class DeliveryActionDefinition
{
    /// <summary>The deliver step that dispatches the route's rendered payload.</summary>
    public ModuleStepDefinition Deliver { get; init; } = new();

    /// <summary>
    /// Optional compensating action to undo THIS specific delivery if a later route/deliver in the same
    /// <see cref="DeliveryRouteDefinition.AtomicScope"/> fails permanently. Reuses the existing
    /// <see cref="IDeliverModule"/> contract so any deliver module (a "cancel" HTTP call, a compensating file write,
    /// etc.) can act as a compensating action without a new module interface.
    /// </summary>
    public ModuleStepDefinition? Compensation { get; init; }
}