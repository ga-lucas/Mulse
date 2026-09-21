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

    Task<RuntimeMulseState> UpsertRetryStateAsync(Mulse.Modules.FlowRetryState retryState, CancellationToken cancellationToken);

    Task<RuntimeMulseState> DeleteRetryStateAsync(string flowId, string executionId, string stageId, CancellationToken cancellationToken);

    Task<RuntimeMulseState> DeleteAllRetryStateForExecutionAsync(string flowId, string executionId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets (creating or overwriting) the value that resolves a <c>{{config:reference}}</c> or
    /// <c>{{secret:reference}}</c> placeholder token with the given reference name.
    /// </summary>
    Task<RuntimeMulseState> SetConfigValueAsync(string reference, string value, CancellationToken cancellationToken);

    /// <summary>Removes a previously-set config/secret value.</summary>
    Task<RuntimeMulseState> DeleteConfigValueAsync(string reference, CancellationToken cancellationToken);
}
