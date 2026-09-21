namespace Mulse.Modules;

/// <summary>
/// Durable record of a flow execution stalled on a step that failed but still has retry attempts remaining.
/// Persisted to disk (see <see cref="IFlowRetryStateStore"/>) before waiting for the next attempt so that a
/// background driver can resume the exact same stage - with the exact same input batch - even if the service
/// crashes or restarts before the delay elapses. This is what makes retries recoverable across restarts rather
/// than only within a single in-memory run.
/// </summary>
public sealed class FlowRetryState
{
    public string FlowId { get; init; } = string.Empty;

    public string ExecutionId { get; init; } = string.Empty;

    /// <summary>
    /// Identifies which stage of the flow is stalled: "fetch", "parse", "augment:{index}",
    /// "delivery:{routeIndex}:render", or "delivery:{routeIndex}:deliver".
    /// </summary>
    public string StageId { get; init; } = string.Empty;

    /// <summary>Number of attempts already made for this stage (including the failed one that triggered persistence).</summary>
    public int AttemptsMade { get; init; }

    public DateTimeOffset NextAttemptAt { get; init; }

    public string? LastError { get; init; }

    /// <summary>When the overall flow execution originally started, preserved across resumes for the final result.</summary>
    public DateTimeOffset ExecutionStartedAt { get; init; }

    /// <summary>The batch this stage should be (re)invoked with when it is retried.</summary>
    public List<FlowOrchestrationPayloadSnapshot> InputPayloads { get; init; } = [];

    /// <summary>
    /// For a stalled delivery route stage (render or deliver) only: the batch produced by the last augment (or
    /// parse, if there are no augments), needed so that later delivery routes' render stages can be reached
    /// without re-running fetch/parse/augments after a resume.
    /// </summary>
    public List<FlowOrchestrationPayloadSnapshot>? PostAugmentPayloads { get; init; }

    /// <summary>Atomic-scope deliveries already committed earlier in this same execution, for compensation on eventual failure.</summary>
    public List<FlowRetryCommittedDelivery> CommittedAtomicDeliveries { get; init; } = [];
}
