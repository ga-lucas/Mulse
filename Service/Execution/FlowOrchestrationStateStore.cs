using Mulse.Modules;

namespace Service.Execution;

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

    public Task<IReadOnlyList<FetchProcessingReceipt>> GetPendingSourceReceiptsAsync(string flowId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<FetchProcessingReceipt> receipts = runtimeConfigurationStore.GetState().OrchestrationCheckpoints
            .Where(checkpoint => checkpoint.FlowId.Equals(flowId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(checkpoint => checkpoint.SourceReceipts).DistinctBy(receipt => receipt.Id).ToArray();
        return Task.FromResult(receipts);
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
