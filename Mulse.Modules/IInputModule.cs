namespace Mulse.Modules;

public interface IInputModule : IModule
{
    Task<IntegrationBatch> ReadAsync(
        FlowExecutionContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
