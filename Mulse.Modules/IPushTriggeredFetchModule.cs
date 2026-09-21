namespace Mulse.Modules;

public interface IPushTriggeredFetchModule : IFetchModule
{
    Task<IAsyncDisposable> RegisterTriggerAsync(
        PushFlowTriggerContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
