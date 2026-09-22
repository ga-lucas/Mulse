using Mulse.Modules;

namespace Service.Execution;

public sealed class FlowRetryStateStore(IRuntimeConfigurationStore runtimeConfigurationStore) : IFlowRetryStateStore
{
    public Task<IReadOnlyList<FlowRetryState>> GetAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FlowRetryState> retries = runtimeConfigurationStore.GetState().PendingRetries;
        return Task.FromResult(retries);
    }

    public async Task UpsertAsync(FlowRetryState state, CancellationToken cancellationToken)
    {
        await runtimeConfigurationStore.UpsertRetryStateAsync(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string flowId, string executionId, string stageId, CancellationToken cancellationToken)
    {
        await runtimeConfigurationStore.DeleteRetryStateAsync(flowId, executionId, stageId, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAllForExecutionAsync(string flowId, string executionId, CancellationToken cancellationToken)
    {
        await runtimeConfigurationStore.DeleteAllRetryStateForExecutionAsync(flowId, executionId, cancellationToken).ConfigureAwait(false);
    }
}
