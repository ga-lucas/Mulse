using Mulse.Modules;

namespace Service;

/// <summary>
/// Polls <see cref="IFlowRetryStateStore"/> for pending retries whose delay has elapsed and resumes them via
/// <see cref="IFlowRuntime.ResumeRetryAsync"/>. Because pending retries are persisted to disk as soon as they
/// are scheduled, this same polling loop is what recovers in-flight work left behind by a service crash or
/// restart: on the very first poll after startup, any retry with a past-due <c>NextAttemptAt</c> (including one
/// that was already due before the restart) is picked up and resumed exactly as it would be during normal
/// operation - no separate "recovery" code path is needed.
/// </summary>
public sealed class RetryDrivenFlowService(
    IFlowRuntime flowRuntime,
    IFlowRetryStateStore flowRetryStateStore,
    TimeProvider timeProvider,
    ILogger<RetryDrivenFlowService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ResumeDueRetriesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(exception, "Retry driver poll failed unexpectedly; will retry on the next poll.");
            }

            await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ResumeDueRetriesAsync(CancellationToken stoppingToken)
    {
        var now = timeProvider.GetUtcNow();
        var pendingRetries = await flowRetryStateStore.GetAllAsync(stoppingToken).ConfigureAwait(false);
        var dueRetries = pendingRetries.Where(retry => retry.NextAttemptAt <= now).ToArray();

        foreach (var retryState in dueRetries)
        {
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var result = await flowRuntime.ResumeRetryAsync(retryState, stoppingToken).ConfigureAwait(false);
                if (result is null)
                {
                    continue;
                }

                logger.LogInformation(
                    "Resumed flow {FlowId} execution {ExecutionId} stage {StageId}: outcome {Outcome}.",
                    retryState.FlowId, retryState.ExecutionId, retryState.StageId, result.Outcome);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogError(
                    exception,
                    "Unexpected error resuming flow {FlowId} execution {ExecutionId} stage {StageId}.",
                    retryState.FlowId, retryState.ExecutionId, retryState.StageId);
            }
        }
    }
}
