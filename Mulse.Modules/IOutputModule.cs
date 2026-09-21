namespace Mulse.Modules;

public interface IOutputModule : IModule
{
    Task WriteAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
