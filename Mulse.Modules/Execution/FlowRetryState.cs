namespace Mulse.Modules.Execution;

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
    /// Identifies which stage of the flow is stalled: "fetch:{fetchNodeId}", "parse:{parseNodeId}",
    /// "augment:{index}", "delivery:{routeIndex}:render", "delivery:{routeIndex}:deliver:{deliverIndex}", or "source-outcome".
    /// </summary>
    public string StageId { get; init; } = string.Empty;

    /// <summary>Number of attempts already made for this stage (including the failed one that triggered persistence).</summary>
    public int AttemptsMade { get; init; }

    public DateTimeOffset NextAttemptAt { get; init; }

    public string? LastError { get; init; }

    /// <summary>When the overall flow execution originally started, preserved across resumes for the final result.</summary>
    public DateTimeOffset ExecutionStartedAt { get; init; }

    /// <summary>
    /// The batch this stage should be (re)invoked with when it is retried. For a deliver stage this is the route's
    /// full rendered batch (before that deliver action's own conditions are applied), so the remaining deliver
    /// actions of the same route can still run after the resumed one.
    /// </summary>
    public List<FlowOrchestrationPayloadSnapshot> InputPayloads { get; init; } = [];

    /// <summary>
    /// For a stalled source-graph stage only: the RAW batches of every fetch node that had already been resolved
    /// before the stalled stage ran, keyed by fetch node id. Lets a resume pick up mid-graph without re-fetching
    /// upstream fetch nodes (which may have side effects, such as deleting the files they consumed).
    /// </summary>
    public Dictionary<string, List<FlowOrchestrationPayloadSnapshot>> ResolvedFetchPayloads { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For a stalled source-graph stage only: the PARSED (and <c>sourceId</c>-tagged) batches of every parse node
    /// that had already been resolved before the stalled stage ran, keyed by parse node id.
    /// </summary>
    public Dictionary<string, List<FlowOrchestrationPayloadSnapshot>> ResolvedParsePayloads { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// For a stalled delivery route stage (render or deliver) only: the batch produced by the last augment (or
    /// parse, if there are no augments), needed so that later delivery routes' render stages can be reached
    /// without re-running fetch/parse/augments after a resume.
    /// </summary>
    public List<FlowOrchestrationPayloadSnapshot>? PostAugmentPayloads { get; init; }

    /// <summary>Atomic-scope deliveries already committed earlier in this same execution, for compensation on eventual failure.</summary>
    public List<FlowRetryCommittedDelivery> CommittedAtomicDeliveries { get; init; } = [];

    /// <summary>
    /// Atomic scopes that already failed permanently (and were compensated) earlier in this same execution, so a
    /// resume keeps skipping their remaining deliver actions and still reports the execution as failed at the end.
    /// </summary>
    public List<string> FailedAtomicScopes { get; init; } = [];

    public List<FetchProcessingReceipt> SourceReceipts { get; init; } = [];
    public List<string> DeliveryFailures { get; init; } = [];
    public List<FlowOrchestrationPayloadSnapshot> Responses { get; init; } = [];
    public string? CheckpointFlowId { get; init; }
    public string? CheckpointCorrelationKey { get; init; }

    /// <summary>Present only for a source-outcome stage; resumes callbacks, never processing or deliveries.</summary>
    public FlowSourceCompletion? SourceCompletion { get; init; }
    public int SourceReceiptIndex { get; init; }
}
