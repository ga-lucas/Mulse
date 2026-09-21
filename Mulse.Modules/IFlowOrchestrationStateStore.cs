namespace Mulse.Modules;

public interface IFlowOrchestrationStateStore
{
    Task<FlowOrchestrationCheckpoint?> GetAsync(string flowId, string correlationKey, CancellationToken cancellationToken);

    Task UpsertAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken);

    Task DeleteAsync(string flowId, string correlationKey, CancellationToken cancellationToken);
}
