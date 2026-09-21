namespace Mulse.Modules;

/// <summary>Persists and retrieves pending flow retry state, keyed by flow + execution id + stage.</summary>
public interface IFlowRetryStateStore
{
    /// <summary>Returns every currently-pending retry (used by the background recovery driver to find due work).</summary>
    Task<IReadOnlyList<FlowRetryState>> GetAllAsync(CancellationToken cancellationToken);

    Task UpsertAsync(FlowRetryState state, CancellationToken cancellationToken);

    Task DeleteAsync(string flowId, string executionId, string stageId, CancellationToken cancellationToken);

    /// <summary>Deletes every pending retry for a given execution, e.g. once the execution finally completes or permanently fails.</summary>
    Task DeleteAllForExecutionAsync(string flowId, string executionId, CancellationToken cancellationToken);
}
