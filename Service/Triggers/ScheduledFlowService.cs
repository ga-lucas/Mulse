using Mulse.Modules;

namespace Service.Triggers;

public sealed class ScheduledFlowService(
    IFlowRuntime flowRuntime,
    TimeProvider timeProvider,
    ILogger<ScheduledFlowService> logger) : BackgroundService
{
    private readonly Dictionary<string, DateTimeOffset> _nextRunByFlowId = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = timeProvider.GetUtcNow();
            var intervalFlows = flowRuntime.GetConfiguredFlows()
                .Where(static flow => flow.Enabled && flow.Trigger.Mode == PipelineTriggerMode.Interval && flow.Trigger.Interval is not null)
                .ToArray();

            ReconcileKnownFlows(intervalFlows.Select(static flow => flow.Id));

            foreach (var flow in intervalFlows)
            {
                if (flow.Trigger.Interval is not { } interval || interval <= TimeSpan.Zero)
                {
                    continue;
                }

                if (!_nextRunByFlowId.TryGetValue(flow.Id, out var dueAt))
                {
                    dueAt = flow.Trigger.RunOnStartup ? now : now.Add(interval);
                    _nextRunByFlowId[flow.Id] = dueAt;
                }

                if (dueAt > now)
                {
                    continue;
                }

                try
                {
                    await flowRuntime.ExecuteAsync(flow.Id, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Scheduled execution failed for flow {FlowId}.", flow.Id);
                }
                finally
                {
                    _nextRunByFlowId[flow.Id] = timeProvider.GetUtcNow().Add(interval);
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        }
    }

    private void ReconcileKnownFlows(IEnumerable<string> configuredFlowIds)
    {
        var activeIds = configuredFlowIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedIds = _nextRunByFlowId.Keys.Where(flowId => !activeIds.Contains(flowId)).ToArray();

        foreach (var removedId in removedIds)
        {
            _nextRunByFlowId.Remove(removedId);
        }
    }
}
