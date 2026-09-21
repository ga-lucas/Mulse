namespace Mulse.Modules;

public interface IDeliverModule : IModule
{
    Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
