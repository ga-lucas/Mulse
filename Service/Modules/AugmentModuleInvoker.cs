using Mulse.Modules.Abstractions;

namespace Service.Modules;

internal sealed class AugmentModuleInvoker(IServiceProvider services) : IAugmentModuleInvoker
{
    public async Task<IntegrationBatch> AugmentAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        await using var lease = await services.GetRequiredService<IModuleCatalog>().LeaseOrchestrationAugmentAsync(moduleId, cancellationToken).ConfigureAwait(false);
        return await lease.Module.AugmentAsync(context, batch, new ModuleStepDefinition
        {
            Module = moduleId, Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        }, cancellationToken).ConfigureAwait(false);
    }
}
