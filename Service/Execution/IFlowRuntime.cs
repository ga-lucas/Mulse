using Mulse.Modules;

namespace Service.Execution;

public interface IFlowRuntime
{
    IReadOnlyList<PipelineDefinition> GetConfiguredFlows();

    Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a stalled stage from a persisted <see cref="FlowRetryState"/> (called by the background retry
    /// driver once its delay has elapsed, or at service startup to recover in-flight work after a crash).
    /// Returns <c>null</c> if the flow no longer exists or is disabled; in that case the stale retry entry has
    /// already been removed.
    /// </summary>
    Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retryState, CancellationToken cancellationToken);
}
