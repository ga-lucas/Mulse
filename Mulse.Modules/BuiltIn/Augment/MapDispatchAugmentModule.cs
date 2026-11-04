using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Mapping;

namespace Mulse.Modules.BuiltIn.Augment;

public sealed class MapDispatchAugmentModule : IOrchestrationAugmentModule
{
    private readonly RenderDispatchModule _dispatch;

    public MapDispatchAugmentModule(IRenderModuleInvoker invoker)
    {
        _dispatch = new(invoker);
        Descriptor = new(RenderDispatchDefinition.AugmentModuleId, "Select map during processing", ModuleKind.OrchestrationAugment,
            "Uses metadata to select an allowlisted renderer/map mid-flow. Its output replaces the working payload for later augments and delivery. Supports per-payload or explicit in-run grouping; missing selections and map failures fail explicitly.",
            _dispatch.Descriptor.Settings);
    }

    public ModuleDescriptor Descriptor { get; }

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
        => _dispatch.RenderAsync(context, batch, step, cancellationToken);
}
