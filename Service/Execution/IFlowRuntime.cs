using Mulse.Modules;

namespace Service.Execution;

public interface IFlowRuntime
{
    IReadOnlyList<PipelineDefinition> GetConfiguredFlows();

    Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken cancellationToken);

    /// <summary>
    /// Resumes a stalled stage from a persisted <see cref="FlowRetryState"/> (called by the background retry
    /// driver once its delay has elapsed, or at service startup to recover in-flight work after a crash).
    /// Returns <c>null</c> when work was removed, is exhausted, or cannot currently resume. Source-bearing
    /// processing retries are retained while a flow is disabled; terminal callbacks retain original settings.
    /// </summary>
    Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retryState, CancellationToken cancellationToken);

    /// <summary>Explicitly retry exhausted source notifications, without fetching or delivering data again.</summary>
    Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken cancellationToken);
}
