namespace Mulse.Modules.Abstractions;

/// <summary>Invokes a registered augment under a host-managed package lease.</summary>
public interface IAugmentModuleInvoker
{
    Task<IntegrationBatch> AugmentAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken);
}
