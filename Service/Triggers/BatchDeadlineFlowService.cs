namespace Service.Triggers;

public sealed class BatchDeadlineFlowService(IFlowRuntime runtime, ILogger<BatchDeadlineFlowService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                foreach (var flow in runtime.GetConfiguredFlows().Where(flow => flow.Enabled && flow.Migration is null
                    && flow.Augments.Any(step => step.Module.Equals("durable-batch-augment", StringComparison.OrdinalIgnoreCase))))
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    try { await runtime.ReleaseDueBatchAsync(flow.Id, stoppingToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    { logger.LogError(ex, "Due-batch check failed for flow {FlowId}; pending data is retained for review/retry.", flow.Id); }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { logger.LogError(ex, "Batch deadline poll failed; the next poll will check persisted deadlines again."); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken).ConfigureAwait(false);
        }
    }
}
