using Mulse.Modules.Abstractions;

namespace Service.Modules;

internal sealed class DeliverModuleInvoker(IServiceProvider services) : IDeliverModuleInvoker
{
    public async Task<IntegrationBatch> DeliverAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch,
        IReadOnlyDictionary<string, string> settings, CancellationToken cancellationToken)
    {
        var step = new ModuleStepDefinition { Module = moduleId, Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase) };
        var expectsResponse = Mulse.Modules.Sdk.ModuleSettingReader.GetBoolean(step.Settings, "expectsResponse", false, moduleId);
        await using var lease = await services.GetRequiredService<IModuleCatalog>().LeaseDeliverAsync(moduleId, cancellationToken).ConfigureAwait(false);
        if (expectsResponse)
        {
            if (lease.Module is not IRequestResponseDeliverModule requestResponse)
                throw new NotSupportedException($"Delivery module '{moduleId}' does not implement the requested response contract.");
            return await requestResponse.DeliverAndCaptureResponseAsync(context, batch, step, cancellationToken).ConfigureAwait(false);
        }
        await lease.Module.DeliverAsync(context, batch, step, cancellationToken).ConfigureAwait(false);
        return IntegrationBatch.Empty;
    }
}
