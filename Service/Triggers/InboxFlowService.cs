using Mulse.Persistence;

namespace Service.Triggers;

public sealed class InboxFlowService(FlowJournal journal, IFlowRuntime runtime, TimeProvider timeProvider, ILogger<InboxFlowService> logger, ProcessingCoordinator? processing = null) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await journal.RecoverAsync(cancellationToken).ConfigureAwait(false);
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var nextPurge = timeProvider.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (processing?.IsAccepting == false)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
                    continue;
                }
                var channels = (await journal.PendingChannelsAsync(stoppingToken).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
                foreach (var flow in runtime.GetConfiguredFlows().Where(flow => flow.Enabled && flow.Migration is null && flow.Trigger.Mode == PipelineTriggerMode.Push))
                {
                    var pending = flow.Fetches.Any(fetch => fetch.Fetch.Module.Equals("http-inbound-fetch", StringComparison.OrdinalIgnoreCase)
                        ? channels.Contains(WorkspaceDocument.Identity("http:" + fetch.Fetch.Settings.GetValueOrDefault("route", "").Trim('/')))
                        : fetch.Fetch.Module.Equals("flow-link-fetch", StringComparison.OrdinalIgnoreCase)
                            && channels.Contains(WorkspaceDocument.Identity("flow-link:" + fetch.Fetch.Settings.GetValueOrDefault("channel", ""))));
                    if (processing?.IsAccepting == false) break;
                    if (!pending) continue;
                    try { await runtime.ExecuteAsync(flow.Id, stoppingToken).ConfigureAwait(false); }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    { logger.LogError(exception, "Durable inbox processing failed for flow {FlowId}; pending and held messages remain persisted.", flow.Id); }
                }
                if (timeProvider.GetUtcNow() >= nextPurge)
                {
                    var count = await journal.PurgeAsync(stoppingToken).ConfigureAwait(false);
                    if (count > 0) logger.LogInformation("Expired {Count} terminal execution histories under the configured retention policy.", count);
                    nextPurge = timeProvider.GetUtcNow().AddHours(1);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { logger.LogError(exception, "Durable inbox poll failed; no storage fallback or automatic replay was performed."); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }
}
