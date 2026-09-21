namespace Mulse.Modules;

public sealed class DeliveryRouteDefinition
{
    public ModuleStepDefinition Render { get; init; } = new();

    public ModuleStepDefinition Deliver { get; init; } = new();

    /// <summary>
    /// Optional atomic-transaction scope name (migrated from BizTalk orchestration <c>AtomicTransaction</c>
    /// scopes). Delivery routes sharing the same non-empty scope within a flow are treated as an all-or-nothing
    /// unit: if a later route in the same scope fails, previously committed routes in that scope are compensated
    /// in reverse order via <see cref="Compensation"/>. Routes with no scope behave exactly as before (independent,
    /// best-effort delivery).
    /// </summary>
    public string? AtomicScope { get; init; }

    /// <summary>
    /// Optional deliver-module step invoked to undo this route's delivery if a later route in the same
    /// <see cref="AtomicScope"/> fails. Reuses the existing <see cref="IDeliverModule"/> contract so any deliver
    /// module (a "cancel" HTTP call, a compensating file write, etc.) can act as a compensating action without a
    /// new module interface.
    /// </summary>
    public ModuleStepDefinition? Compensation { get; init; }
}
