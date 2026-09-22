namespace Mulse.Modules.Abstractions;

public interface IRenderModule : IModule
{
    Task<IntegrationBatch> RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
