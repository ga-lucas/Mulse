namespace Service.Execution;

public sealed class CoordinatedFlowRuntime(IFlowRuntime inner, ProcessingCoordinator coordinator) : IFlowRuntime
{
    public IReadOnlyList<PipelineDefinition> GetConfiguredFlows() => inner.GetConfiguredFlows();

    private async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var admission = coordinator.Enter(execution: true);
        using var execution = CancellationTokenSource.CreateLinkedTokenSource(coordinator.ForcedCancellation);
        using var requestCancellation = cancellationToken.Register(() =>
        {
            if (!coordinator.IsStopping) execution.Cancel();
        });
        return await action(execution.Token).ConfigureAwait(false);
    }

    public Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken cancellationToken)
        => RunAsync(token => inner.ExecuteAsync(flowId, token), cancellationToken);
    public Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flowId, CancellationToken cancellationToken)
        => RunAsync(token => inner.ReleaseDueBatchAsync(flowId, token), cancellationToken);
    public Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flowId, string instanceId, int expectedPayloadCount, CancellationToken cancellationToken)
        => RunAsync(token => inner.ReleaseCollectedBatchAsync(flowId, instanceId, expectedPayloadCount, token), cancellationToken);
    public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retryState, CancellationToken cancellationToken)
        => RunAsync(token => inner.ResumeRetryAsync(retryState, token), cancellationToken);
    public Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken cancellationToken)
        => RunAsync(token => inner.RetrySourceOutcomesAsync(flowId, token), cancellationToken);
    public Task<FlowExecutionResult> ReconcileNativeEffectAsync(string flowId, string instanceId, string expectedEffectId, string evidence, IntegrationBatch? output, CancellationToken cancellationToken)
        => RunAsync(token => inner.ReconcileNativeEffectAsync(flowId, instanceId, expectedEffectId, evidence, output, token), cancellationToken);
}
