namespace Mulse.Modules;

public interface IFetchModule : IModule
{
    Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
