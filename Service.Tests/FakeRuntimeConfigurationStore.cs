using Mulse.Modules;
using Service;

namespace Service.Tests;

/// <summary>Minimal in-memory <see cref="IRuntimeConfigurationStore"/> double for unit tests that only need <see cref="GetState"/>.</summary>
internal sealed class FakeRuntimeConfigurationStore(RuntimeMulseState state) : IRuntimeConfigurationStore
{
    public RuntimeMulseState GetState() => state;

    public Task<RuntimeMulseState> UpsertFlowAsync(PipelineDefinition pipeline, CancellationToken cancellationToken)
    {
        state.Pipelines.RemoveAll(existing => string.Equals(existing.Id, pipeline.Id, StringComparison.OrdinalIgnoreCase));
        state.Pipelines.Add(pipeline);
        return Task.FromResult(state);
    }

    public Task<RuntimeMulseState> DeleteFlowAsync(string flowId, CancellationToken cancellationToken)
    {
        state.Pipelines.RemoveAll(existing => string.Equals(existing.Id, flowId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(state);
    }

    public Task<RuntimeMulseState> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> DeleteManagedPackageAsync(string packageId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> UpsertOrchestrationCheckpointAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        state.OrchestrationCheckpoints.RemoveAll(existing =>
            string.Equals(existing.FlowId, checkpoint.FlowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.CorrelationKey, checkpoint.CorrelationKey, StringComparison.OrdinalIgnoreCase));
        state.OrchestrationCheckpoints.Add(checkpoint);
        return Task.FromResult(state);
    }

    public Task<RuntimeMulseState> DeleteOrchestrationCheckpointAsync(string flowId, string correlationKey, CancellationToken cancellationToken)
    {
        state.OrchestrationCheckpoints.RemoveAll(existing =>
            string.Equals(existing.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.CorrelationKey, correlationKey, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(state);
    }

    public Task<RuntimeMulseState> UpsertRetryStateAsync(FlowRetryState retryState, CancellationToken cancellationToken)
    {
        state.PendingRetries.RemoveAll(existing =>
            string.Equals(existing.FlowId, retryState.FlowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ExecutionId, retryState.ExecutionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.StageId, retryState.StageId, StringComparison.OrdinalIgnoreCase));
        state.PendingRetries.Add(retryState);
        return Task.FromResult(state);
    }

    public Task<RuntimeMulseState> DeleteRetryStateAsync(string flowId, string executionId, string stageId, CancellationToken cancellationToken)
    {
        state.PendingRetries.RemoveAll(existing =>
            string.Equals(existing.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.StageId, stageId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(state);
    }

    public Task<RuntimeMulseState> DeleteAllRetryStateForExecutionAsync(string flowId, string executionId, CancellationToken cancellationToken)
    {
        state.PendingRetries.RemoveAll(existing =>
            string.Equals(existing.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(existing.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(state);
    }

    public Task<RuntimeMulseState> SetConfigValueAsync(string reference, string value, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> DeleteConfigValueAsync(string reference, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
