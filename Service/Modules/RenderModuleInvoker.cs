using Mulse.Modules.Abstractions;

namespace Service.Modules;

internal sealed class RenderModuleInvoker(IServiceProvider services) : IRenderModuleInvoker
{
    public async Task<IntegrationBatch> RenderAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        await using var lease = await services.GetRequiredService<IModuleCatalog>().LeaseRenderAsync(moduleId, cancellationToken).ConfigureAwait(false);
        return await lease.Module.RenderAsync(context, batch, new ModuleStepDefinition
        {
            Module = moduleId, Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        }, cancellationToken).ConfigureAwait(false);
    }
}
