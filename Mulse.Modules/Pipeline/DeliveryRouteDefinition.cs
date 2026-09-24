namespace Mulse.Modules.Pipeline;

/// <summary>
/// One delivery route of a flow: a single <see cref="Render"/> step whose output is handed to every deliver action in
/// <see cref="Delivers"/>. Rendering happens once per route; each deliver action then runs independently
/// (best-effort) against that same rendered output.
/// </summary>
public sealed class DeliveryRouteDefinition
{
    public ModuleStepDefinition Render { get; init; } = new();

    /// <summary>
    /// The deliver actions (at least one) that each receive this route's rendered output. A permanent failure of one
    /// deliver action does not stop the remaining actions of the route unless the route has an
    /// <see cref="AtomicScope"/>.
    /// </summary>
    public IReadOnlyList<DeliveryActionDefinition> Delivers { get; init; } = [];

    /// <summary>
    /// Optional atomic-transaction scope name (migrated from BizTalk orchestration <c>AtomicTransaction</c>
    /// scopes), applying to every deliver action of this route. Deliver actions of routes sharing the same non-empty
    /// scope within a flow are treated as an all-or-nothing unit: if any of them fails permanently, every previously
    /// committed deliver action in that scope is compensated in reverse order via its
    /// <see cref="DeliveryActionDefinition.Compensation"/>, and the scope's remaining deliver actions are skipped.
    /// Routes with no scope deliver independently and best-effort.
    /// </summary>
    public string? AtomicScope { get; init; }
}