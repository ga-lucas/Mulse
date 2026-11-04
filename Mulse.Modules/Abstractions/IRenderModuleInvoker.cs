namespace Mulse.Modules.Abstractions;

/// <summary>Invokes a registered renderer under a host-managed package lease.</summary>
public interface IRenderModuleInvoker
{
    Task<IntegrationBatch> RenderAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken);
}
