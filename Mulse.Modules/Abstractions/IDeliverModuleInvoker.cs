namespace Mulse.Modules.Abstractions;

/// <summary>Invokes a leased delivery module and returns its response when explicitly requested; otherwise returns an empty batch.</summary>
public interface IDeliverModuleInvoker
{
    Task<IntegrationBatch> DeliverAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken);
}
