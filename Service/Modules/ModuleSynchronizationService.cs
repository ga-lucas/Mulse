namespace Service.Modules;

public sealed class ModuleSynchronizationService(IModuleCatalog moduleCatalog, ILogger<ModuleSynchronizationService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await moduleCatalog.SynchronizeAsync(stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
                await moduleCatalog.SynchronizeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Runtime module synchronization failed.");
            }
        }
    }
}
