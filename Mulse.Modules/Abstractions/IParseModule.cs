namespace Mulse.Modules.Abstractions;

public interface IParseModule : IModule
{
    Task<IntegrationBatch> ParseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
