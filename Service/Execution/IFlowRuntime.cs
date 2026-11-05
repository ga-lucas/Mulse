using Mulse.Modules;

namespace Service.Execution;

public interface IFlowRuntime
{
    IReadOnlyList<PipelineDefinition> GetConfiguredFlows();

    Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken cancellationToken);

    /// <summary>Release one eligible persisted timed batch without fetching; returns null when none is safe/due.</summary>
    Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flowId, CancellationToken cancellationToken);

    /// <summary>Explicit early release of a still-collecting instance at its inspected payload count; never replay released data.</summary>
    Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flowId, string instanceId, int expectedPayloadCount, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a stalled stage from a persisted <see cref="FlowRetryState"/> (called by the background retry
    /// driver once its delay has elapsed, or at service startup to recover in-flight work after a crash).
    /// Returns <c>null</c> when work was removed, is exhausted, or cannot currently resume. Source-bearing
    /// processing retries are retained while a flow is disabled; terminal callbacks retain original settings.
    /// </summary>
    Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retryState, CancellationToken cancellationToken);

    /// <summary>Explicitly retry exhausted source notifications, without fetching or delivering data again.</summary>
    Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken cancellationToken);

    Task<FlowExecutionResult> ReconcileNativeEffectAsync(string flowId, string instanceId, string expectedEffectId, string evidence,
        IntegrationBatch? output, CancellationToken cancellationToken)
        => throw new NotSupportedException("This runtime does not support native effect reconciliation.");
}
