using Mulse.Persistence;

namespace Service.Triggers;

public sealed class WorkspaceOwnershipService(IWorkspaceOwnershipGuard ownership, ProcessingCoordinator processing, ILogger<WorkspaceOwnershipService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { ownership.EnsureAvailable(); }
            catch (Exception exception)
            {
                logger.LogCritical(exception, "Workspace ownership lost. Blocking admission and cancelling active work; uncertain effects require reconciliation.");
                processing.Fail("Workspace ownership lost. Restart to reacquire the database/file ownership lock.");
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }
}
