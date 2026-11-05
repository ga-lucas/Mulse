using Mulse.Modules.Mapping;

namespace Mulse.Modules.BuiltIn.Render;

public sealed class RenderPipelineModule(IRenderModuleInvoker invoker) : IRenderModule
{
    public ModuleDescriptor Descriptor { get; } = new(RenderPipelineDefinition.ModuleId, "Ordered renderer pipeline", ModuleKind.Render,
        "Runs 1-32 renderer stages in order, passing the entire output batch to the next stage. Configure stages visually. This is a native pipeline, not automatic BizTalk component translation.",
        [new("stagesJson", "Renderer stages", "Ordered renderer modules and settings. Nested pipelines and dispatch stages are not supported.", true, ModuleSettingInputKind.TextArea, "[]")]);

    public async Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var stages = RenderPipelineDefinition.Read(ModuleSettingReader.GetRequired(step.Settings, "stagesJson", Descriptor.Id));
        foreach (var stage in stages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batch = await invoker.RenderAsync(stage.Module, context, batch, stage.Settings, cancellationToken).ConfigureAwait(false);
        }
        return batch;
    }
}
