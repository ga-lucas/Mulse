namespace Mulse.Modules.Execution;

public interface IDurableInbox
{
    Task EnqueueAsync(string channel, string? flowId, Guid messageId, IntegrationBatch batch, CancellationToken cancellationToken);
    Task EnqueueConfirmedAsync(string channel, Guid messageId, IntegrationBatch batch, CancellationToken cancellationToken)
        => throw new FlowDurabilityException("This inbox does not support permanently retained confirmed receipts.");
    Task<bool> ContainsAsync(string channel, Guid messageId, IntegrationBatch batch, CancellationToken cancellationToken);
    Task<IntegrationBatch> ClaimAsync(string channel, FlowExecutionContext context, CancellationToken cancellationToken);
    Task CompleteAsync(FlowExecutionContext context, string channel, bool succeeded, CancellationToken cancellationToken);
}

public sealed class FlowDurabilityException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
