namespace Mulse.Modules;

/// <summary>
/// A delivery route's committed output, persisted so that if the flow execution is resumed after a crash (or a
/// later stage's retries are exhausted), the atomic-scope compensation logic in <c>FlowRuntime</c> still knows
/// which prior deliveries in the same scope succeeded and need to be rolled back.
/// </summary>
public sealed class FlowRetryCommittedDelivery
{
    public int RouteIndex { get; init; }

    public string AtomicScope { get; init; } = string.Empty;

    public string? CompensationModule { get; init; }

    public Dictionary<string, string> CompensationSettings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<FlowOrchestrationPayloadSnapshot> DeliveredPayloads { get; init; } = [];
}
