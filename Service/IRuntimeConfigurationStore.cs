namespace Service;

public interface IRuntimeConfigurationStore
{
    RuntimeMulseState GetState();

    Task<RuntimeMulseState> UpsertFlowAsync(Mulse.Modules.PipelineDefinition pipeline, CancellationToken cancellationToken);

    Task<RuntimeMulseState> DeleteFlowAsync(string flowId, CancellationToken cancellationToken);

    Task<RuntimeMulseState> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken);

    Task<RuntimeMulseState> DeleteManagedPackageAsync(string packageId, CancellationToken cancellationToken);

    Task<RuntimeMulseState> UpsertOrchestrationCheckpointAsync(Mulse.Modules.FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken);

    Task<RuntimeMulseState> DeleteOrchestrationCheckpointAsync(string flowId, string correlationKey, CancellationToken cancellationToken);
}
