using System.Runtime.ExceptionServices;

namespace Service.Execution;

public sealed partial class FlowRuntime
{
    private const string SourceOutcomeStage = "source-outcome";

    private async Task<FlowExecutionResult> RunWithSourceOutcomesAsync(
        PipelineDefinition pipeline, FlowExecutionContext context,
        Func<Task<FlowExecutionResult>> run, CancellationToken cancellationToken)
    {
        FlowExecutionResult result;
        try
        {
            result = await run().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (FlowRetryScheduledSignal or OperationCanceledException))
        {
            if (context.SourceReceipts.Count == 0) throw;
            return await CompleteSourcesAsync(pipeline, context, new FlowSourceCompletion
            {
                Outcome = new(false, exception.Message),
                ThrowProcessingFailure = true
            }, 0, 0, cancellationToken, exception).ConfigureAwait(false);
        }

        if (result.Outcome != FlowExecutionOutcome.Completed) return result;
        if (context.SourceReceipts.Count == 0)
        {
            if (context.PendingCheckpointCompletion is { } pending)
                await orchestrationStateStore.DeleteAsync(pending.FlowId, pending.CorrelationKey, cancellationToken).ConfigureAwait(false);
            await flowRetryStateStore.DeleteAllForExecutionAsync(pipeline.Id, context.ExecutionId, cancellationToken).ConfigureAwait(false);
            return result;
        }
        return await CompleteSourcesAsync(pipeline, context, new FlowSourceCompletion
        {
            Outcome = new(context.DeliveryFailures.Count == 0, result.Detail),
            PayloadCount = result.PayloadCount,
            DeliverModules = result.DeliverModules.ToList(),
            Responses = SnapshotBatch(new IntegrationBatch(context.CapturedResponses)),
            CheckpointFlowId = context.PendingCheckpointCompletion?.FlowId,
            CheckpointCorrelationKey = context.PendingCheckpointCompletion?.CorrelationKey
        }, 0, 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FlowExecutionResult> CompleteSourcesAsync(
        PipelineDefinition pipeline, FlowExecutionContext context, FlowSourceCompletion completion,
        int startIndex, int attemptsAlreadyMade, CancellationToken cancellationToken, Exception? originalError = null)
    {
        for (var index = startIndex; index < context.SourceReceipts.Count; index++)
        {
            var receipt = context.SourceReceipts[index];
            if (completion.CompletedReceiptIds.Contains(receipt.Id, StringComparer.Ordinal)) continue;
            var attempts = index == startIndex ? attemptsAlreadyMade : 0;
            // Write intent before invoking. A crash can repeat a callback, but cannot replay deliveries.
            await PersistSourceCompletionAsync(pipeline, context, completion, index, attempts, timeProvider.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
            try
            {
                await using (var lease = await moduleCatalog.LeaseFetchAsync(receipt.Step.Module, cancellationToken).ConfigureAwait(false))
                {
                    if (lease.Module is not IFetchOutcomeModule source)
                        throw new InvalidOperationException($"Fetch module '{receipt.Step.Module}' no longer supports pending source outcomes.");
                    await source.OnProcessingCompletedAsync(context, receipt, completion.Outcome, cancellationToken).ConfigureAwait(false);
                }
                completion.CompletedReceiptIds.Add(receipt.Id);
                logger.LogInformation("Flow {FlowId} execution {ExecutionId} notified source {FetchNodeId} receipt {ReceiptId}: processing succeeded={Succeeded}.",
                    pipeline.Id, context.ExecutionId, receipt.FetchNodeId, receipt.Id, completion.Outcome.Succeeded);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                var attempt = attempts + 1;
                var policy = EffectiveRetry(pipeline, receipt.Step);
                if (policy.AllowsAnotherAttempt(attempt))
                {
                    var next = timeProvider.GetUtcNow().Add(policy.ComputeDelay(attempt + 1));
                    await PersistSourceCompletionAsync(pipeline, context, completion, index, attempt, next, exception.Message, cancellationToken).ConfigureAwait(false);
                    logger.LogWarning(exception, "Flow {FlowId} execution {ExecutionId} source {FetchNodeId} outcome notification failed; retry scheduled without replaying deliveries.",
                        pipeline.Id, context.ExecutionId, receipt.FetchNodeId);
                    throw new FlowRetryScheduledSignal(new FlowExecutionResult(pipeline.Id, context.StartedAt, timeProvider.GetUtcNow(),
                        completion.PayloadCount, completion.DeliverModules, FlowExecutionOutcome.Retrying,
                        $"Source outcome notification for '{receipt.FetchNodeId}' failed: {exception.Message}. Processing outcome: {completion.Outcome.Error ?? "success"}",
                        null, next));
                }
                logger.LogError(exception, "Flow {FlowId} execution {ExecutionId} source {FetchNodeId} outcome notification failed permanently; processing outcome was succeeded={Succeeded}.",
                    pipeline.Id, context.ExecutionId, receipt.FetchNodeId, completion.Outcome.Succeeded);
                completion.CallbackErrors.Add($"Source '{receipt.FetchNodeId}' outcome notification failed: {exception.Message}");
            }
        }

        if (completion.CallbackErrors.Count > 0)
        {
            completion.NotificationsExhausted = true;
            await PersistSourceCompletionAsync(pipeline, context, completion, context.SourceReceipts.Count, 0,
                DateTimeOffset.MaxValue, string.Join("; ", completion.CallbackErrors), cancellationToken).ConfigureAwait(false);
            var errors = completion.CallbackErrors.Select(message => new InvalidOperationException(message)).Cast<Exception>().ToList();
            if (!completion.Outcome.Succeeded)
                errors.Insert(0, originalError ?? new InvalidOperationException(completion.Outcome.Error ?? "Processing failed."));
            throw new AggregateException($"Source outcome notification failed. New runs are blocked to prevent resending data. After resolving the issue, POST /api/flows/{Uri.EscapeDataString(pipeline.Id)}/source-outcomes/retry to retry notifications only.", errors);
        }
        await PersistSourceCompletionAsync(pipeline, context, completion, context.SourceReceipts.Count, 0,
            timeProvider.GetUtcNow(), null, cancellationToken).ConfigureAwait(false);
        if (completion.Outcome.Succeeded && completion.CheckpointFlowId is { } checkpointFlow && completion.CheckpointCorrelationKey is { } checkpointKey)
            await orchestrationStateStore.DeleteAsync(checkpointFlow, checkpointKey, cancellationToken).ConfigureAwait(false);
        await flowRetryStateStore.DeleteAllForExecutionAsync(pipeline.Id, context.ExecutionId, cancellationToken).ConfigureAwait(false);
        if (completion.ThrowProcessingFailure)
        {
            if (originalError is not null) ExceptionDispatchInfo.Capture(originalError).Throw();
            throw new InvalidOperationException(completion.Outcome.Error ?? "Processing failed before source notification.");
        }
        return new FlowExecutionResult(pipeline.Id, context.StartedAt, timeProvider.GetUtcNow(), completion.PayloadCount,
            completion.DeliverModules, FlowExecutionOutcome.Completed, completion.Outcome.Error,
            completion.Responses.Count > 0 ? RestoreBatch(completion.Responses).Payloads : null);
    }

    private Task PersistSourceCompletionAsync(PipelineDefinition pipeline, FlowExecutionContext context, FlowSourceCompletion completion,
        int index, int attempts, DateTimeOffset next, string? error, CancellationToken cancellationToken)
        => flowRetryStateStore.UpsertAsync(new FlowRetryState
        {
            FlowId = pipeline.Id, ExecutionId = context.ExecutionId, ExecutionStartedAt = context.StartedAt,
            StageId = SourceOutcomeStage, AttemptsMade = attempts, NextAttemptAt = next, LastError = error,
            SourceReceipts = context.SourceReceipts.ToList(), DeliveryFailures = context.DeliveryFailures.ToList(),
            SourceCompletion = completion, SourceReceiptIndex = index
        }, cancellationToken);
}
