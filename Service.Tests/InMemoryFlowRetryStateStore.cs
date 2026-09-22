namespace Service.Tests;

/// <summary>In-memory <see cref="IFlowRetryStateStore"/> double for <see cref="FlowRuntime"/> tests. Exposes
/// the current pending states so tests can assert a retry was actually persisted/cleared as expected.</summary>
internal sealed class InMemoryFlowRetryStateStore : IFlowRetryStateStore
{
    private readonly List<FlowRetryState> _states = [];

    public IReadOnlyList<FlowRetryState> States => _states;

    public Task<IReadOnlyList<FlowRetryState>> GetAllAsync(CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<FlowRetryState>>([.. _states]);

    public Task UpsertAsync(FlowRetryState state, CancellationToken cancellationToken)
    {
        _states.RemoveAll(existing =>
            string.Equals(existing.FlowId, state.FlowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ExecutionId, state.ExecutionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.StageId, state.StageId, StringComparison.OrdinalIgnoreCase));
        _states.Add(state);
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string flowId, string executionId, string stageId, CancellationToken cancellationToken)
    {
        _states.RemoveAll(existing =>
            string.Equals(existing.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.StageId, stageId, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }

    public Task DeleteAllForExecutionAsync(string flowId, string executionId, CancellationToken cancellationToken)
    {
        _states.RemoveAll(existing =>
            string.Equals(existing.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase));
        return Task.CompletedTask;
    }
}
