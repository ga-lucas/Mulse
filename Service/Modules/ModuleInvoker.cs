using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Mulse.Modules;

namespace Service.Modules;

/// <summary>
/// Host implementation of <see cref="IModuleInvoker"/>: leases the requested transform module from the
/// <see cref="IModuleCatalog"/> for the duration of a single invocation.
/// </summary>
/// <remarks>
/// The catalog is resolved lazily rather than injected directly because <see cref="ModuleCatalog"/> instantiates
/// every registered module (to read its descriptor) while it is itself being constructed. A module that depends on
/// <see cref="IModuleInvoker"/> (e.g. the join/map augment) would otherwise force the DI container to resolve the
/// catalog recursively from inside its own constructor.
/// </remarks>
internal sealed class ModuleInvoker(IServiceProvider serviceProvider) : IModuleInvoker
{
    public async Task<JsonNode?> InvokeTransformAsync(
        string moduleId,
        FlowExecutionContext context,
        IReadOnlyList<JsonNode?> inputs,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken)
    {
        var moduleCatalog = serviceProvider.GetRequiredService<IModuleCatalog>();
        await using var lease = await moduleCatalog.LeaseTransformAsync(moduleId, cancellationToken).ConfigureAwait(false);
        var step = new ModuleStepDefinition
        {
            Module = moduleId,
            Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)
        };

        return await lease.Module.InvokeAsync(context, inputs, step, cancellationToken).ConfigureAwait(false);
    }
}
