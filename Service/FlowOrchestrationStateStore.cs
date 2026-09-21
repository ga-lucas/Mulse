using Mulse.Modules;

namespace Service;

public sealed class FlowOrchestrationStateStore(IRuntimeConfigurationStore runtimeConfigurationStore) : IFlowOrchestrationStateStore
{
    public Task<FlowOrchestrationCheckpoint?> GetAsync(string flowId, string correlationKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var checkpoint = runtimeConfigurationStore.GetState().OrchestrationCheckpoints.FirstOrDefault(candidate =>
            string.Equals(candidate.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.CorrelationKey, correlationKey, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(checkpoint);
    }

    public async Task UpsertAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await runtimeConfigurationStore.UpsertOrchestrationCheckpointAsync(checkpoint, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string flowId, string correlationKey, CancellationToken cancellationToken)
    {
        await runtimeConfigurationStore.DeleteOrchestrationCheckpointAsync(flowId, correlationKey, cancellationToken).ConfigureAwait(false);
    }
}
