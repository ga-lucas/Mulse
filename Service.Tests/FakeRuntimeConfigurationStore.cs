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
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> DeleteOrchestrationCheckpointAsync(string flowId, string correlationKey, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> UpsertRetryStateAsync(FlowRetryState retryState, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> DeleteRetryStateAsync(string flowId, string executionId, string stageId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> DeleteAllRetryStateForExecutionAsync(string flowId, string executionId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> SetConfigValueAsync(string reference, string value, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<RuntimeMulseState> DeleteConfigValueAsync(string reference, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
