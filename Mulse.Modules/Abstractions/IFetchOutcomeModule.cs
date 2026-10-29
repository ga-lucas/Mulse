namespace Mulse.Modules.Abstractions;

/// <summary>Optional source finalization, invoked by the runtime only after a terminal processing outcome.
/// Implementations must be idempotent: notification retries may use a new module instance, including after restart.
/// A failed outcome does not imply that no destinations received data.</summary>
public interface IFetchOutcomeModule : IFetchModule
{
    Task OnProcessingCompletedAsync(
        FlowExecutionContext context,
        FetchProcessingReceipt receipt,
        FetchProcessingOutcome outcome,
        CancellationToken cancellationToken);
}
