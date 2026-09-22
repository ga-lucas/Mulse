namespace Service.Tests;

/// <summary>In-memory <see cref="IFlowOrchestrationStateStore"/> double for <see cref="FlowRuntime"/> tests.</summary>
internal sealed class InMemoryFlowOrchestrationStateStore : IFlowOrchestrationStateStore
{
    private readonly List<FlowOrchestrationCheckpoint> _checkpoints = [];

    public IReadOnlyList<FlowOrchestrationCheckpoint> Checkpoints => _checkpoints;

    public Task<FlowOrchestrationCheckpoint?> GetAsync(string flowId, string correlationKey, CancellationToken cancellationToken)
    {
        var checkpoint = _checkpoints.FirstOrDefault(candidate =>
            string.Equals(candidate.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(candidate.CorrelationKey, correlationKey, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(checkpoint);
    }

    public Task UpsertAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        _checkpoints.RemoveAll(existing =>
            string.Equals(existing.FlowId, checkpoint.FlowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.CorrelationKey, checkpoint.CorrelationKey, StringComparison.OrdinalIgnoreCase));
        _checkpoints.Add(checkpoint);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string flowId, string correlationKey, CancellationToken cancellationToken)
    {
        _checkpoints.RemoveAll(existing =>
            string.Equals(existing.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.CorrelationKey, correlationKey, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }
}
