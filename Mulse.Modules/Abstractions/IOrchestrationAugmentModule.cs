namespace Mulse.Modules.Abstractions;

public interface IOrchestrationAugmentModule : IModule
{
    Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
