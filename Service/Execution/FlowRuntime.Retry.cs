using Mulse.Modules;

namespace Service.Execution;

public sealed partial class FlowRuntime
{
    /// <summary>
    /// Resumes a previously persisted <see cref="FlowRetryState"/>, either because its delay has elapsed or
    /// because the service is recovering pending work found on disk after a restart. Returns <c>null</c> (after
    /// deleting stale retry entries) if the flow has been deleted. Source-bearing processing retries stay
    /// pending while a flow is disabled; terminal source callbacks use their original settings even if it is disabled.
    /// </summary>
    public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retryState, CancellationToken cancellationToken)
        => ResumeRetryCoreAsync(retryState, restartSourceNotifications: false, cancellationToken);

    public async Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken cancellationToken)
    {
        var state = (await flowRetryStateStore.GetAllAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(candidate => candidate.FlowId.Equals(flowId, StringComparison.OrdinalIgnoreCase) && candidate.SourceCompletion?.NotificationsExhausted == true)
            ?? throw new InvalidOperationException($"Flow '{flowId}' has no exhausted source outcome notifications to retry.");
        return await ResumeRetryCoreAsync(state, restartSourceNotifications: true, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The source outcome state was removed or another caller already retried it.");
    }

    private async Task<FlowExecutionResult?> ResumeRetryCoreAsync(FlowRetryState retryState, bool restartSourceNotifications, CancellationToken cancellationToken)
    {
        PipelineDefinition pipeline;
        try
        {
            pipeline = retryState.SourceCompletion is not null
                ? new PipelineDefinition
                {
                    Id = flowDefinitionService.GetById(retryState.FlowId).Id,
                    Enabled = false,
                    Fetches = retryState.SourceReceipts.Select((receipt, index) => new FetchNodeDefinition { Id = $"receipt-{index}", Fetch = receipt.Step }).ToList()
                }
                : GetConfiguredFlow(retryState.FlowId);
        }
        catch (Exception exception) when (exception is not FlowMigrationBlockedException && (exception is KeyNotFoundException or InvalidOperationException))
        {
            logger.LogWarning(
                "Cannot resume pending retry for flow {FlowId} execution {ExecutionId} stage {StageId}: {Reason}",
                retryState.FlowId, retryState.ExecutionId, retryState.StageId, exception.Message);
            if (retryState.SourceReceipts.Count == 0 || exception is KeyNotFoundException)
                await flowRetryStateStore.DeleteAllForExecutionAsync(retryState.FlowId, retryState.ExecutionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var gate = _flowLocks.GetOrAdd(pipeline.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await flowRetryStateStore.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(candidate => candidate.FlowId.Equals(retryState.FlowId, StringComparison.OrdinalIgnoreCase)
                    && candidate.ExecutionId == retryState.ExecutionId && candidate.StageId == retryState.StageId);
            if (current is null || (!restartSourceNotifications && current.SourceCompletion?.NotificationsExhausted == true)) return null;
            if (restartSourceNotifications && current.SourceCompletion?.NotificationsExhausted != true) return null;
            retryState = current;
            if (retryState.SourceCompletion is null)
                pipeline = GetConfiguredFlow(retryState.FlowId);
            using var moduleUsage = moduleCatalog.PinFlowModules(pipeline);
            var context = new FlowExecutionContext(pipeline.Id, retryState.ExecutionId, retryState.ExecutionStartedAt)
            {
                PendingSourceReceipts = await orchestrationStateStore.GetPendingSourceReceiptsAsync(pipeline.Id, cancellationToken).ConfigureAwait(false)
            };
            foreach (var receipt in retryState.SourceReceipts) context.RegisterSourceReceipt(receipt);
            foreach (var failure in retryState.DeliveryFailures) context.RecordDeliveryFailure(failure);
            context.CaptureResponse(RestoreBatch(retryState.Responses));
            if (retryState.CheckpointFlowId is { } checkpointFlow && retryState.CheckpointCorrelationKey is { } checkpointKey)
                context.MarkCheckpointForCompletion(checkpointFlow, checkpointKey);
            var committedAtomicDeliveries = new List<FlowRetryCommittedDelivery>(retryState.CommittedAtomicDeliveries);
            var inputBatch = RestoreBatch(retryState.InputPayloads);

            async Task<FlowExecutionResult> ResumeStagesAsync()
            {
                if (IsSourceGraphStageId(retryState.StageId))
                {
                    return await RunSourceGraphOnwardAsync(
                        pipeline, context, retryState.ExecutionStartedAt,
                        RestoreResolvedBatches(retryState.ResolvedFetchPayloads),
                        RestoreResolvedBatches(retryState.ResolvedParsePayloads),
                        retryState.StageId, inputBatch, retryState.AttemptsMade,
                        cancellationToken).ConfigureAwait(false);
                }

                if (retryState.StageId.StartsWith("augment:", StringComparison.Ordinal))
                {
                    var augmentIndex = int.Parse(retryState.StageId["augment:".Length..]);
                    return await RunAugmentOnwardAsync(pipeline, context, retryState.ExecutionStartedAt, augmentIndex, inputBatch, retryState.AttemptsMade, cancellationToken).ConfigureAwait(false);
                }

                var parts = retryState.StageId.Split(':');
                var routeIndex = int.Parse(parts[1]);
                var postAugmentBatch = RestoreBatch(retryState.PostAugmentPayloads ?? []);
                var failedAtomicScopes = new HashSet<string>(retryState.FailedAtomicScopes, StringComparer.Ordinal);

                if (string.Equals(parts[2], "render", StringComparison.Ordinal))
                {
                    return await RunDeliveryLoopAsync(
                        pipeline, context, retryState.ExecutionStartedAt, postAugmentBatch, routeIndex,
                        resumeRenderInput: inputBatch, resumeRenderAttempts: retryState.AttemptsMade,
                        resumeDeliverIndex: null, resumeRenderedBatch: null, resumeDeliverAttempts: 0,
                        committedAtomicDeliveries, failedAtomicScopes, cancellationToken).ConfigureAwait(false);
                }

                // "delivery:{route}:deliver:{deliverIndex}"; a deliver stage's snapshotted input is the route's full
                // rendered batch, so the resumed deliver action and every later one of the same route can run from it.
                var deliverIndex = parts.Length > 3 ? int.Parse(parts[3]) : 0;
                return await RunDeliveryLoopAsync(
                    pipeline, context, retryState.ExecutionStartedAt, postAugmentBatch, routeIndex,
                    resumeRenderInput: null, resumeRenderAttempts: 0,
                    resumeDeliverIndex: deliverIndex, resumeRenderedBatch: inputBatch, resumeDeliverAttempts: retryState.AttemptsMade,
                    committedAtomicDeliveries, failedAtomicScopes, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                if (retryState.SourceCompletion is { } completion)
                {
                    if (restartSourceNotifications)
                        completion = new FlowSourceCompletion
                        {
                            Outcome = completion.Outcome, ThrowProcessingFailure = completion.ThrowProcessingFailure,
                            PayloadCount = completion.PayloadCount, DeliverModules = completion.DeliverModules,
                            Responses = completion.Responses, CompletedReceiptIds = completion.CompletedReceiptIds.ToList(),
                            CheckpointFlowId = completion.CheckpointFlowId, CheckpointCorrelationKey = completion.CheckpointCorrelationKey
                        };
                    return await CompleteSourcesAsync(pipeline, context, completion,
                        restartSourceNotifications ? 0 : retryState.SourceReceiptIndex,
                        restartSourceNotifications ? 0 : retryState.AttemptsMade, cancellationToken).ConfigureAwait(false);
                }
                return await RunWithSourceOutcomesAsync(pipeline, context, ResumeStagesAsync, cancellationToken).ConfigureAwait(false);
            }
            catch (FlowRetryScheduledSignal signal)
            {
                return signal.Result;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private static RetryPolicyDefinition EffectiveRetry(PipelineDefinition pipeline, ModuleStepDefinition step)
        => step.Retry ?? pipeline.Retry ?? RetryPolicyDefinition.NoRetry;

    /// <summary>Control-flow-only signal: a retry was scheduled and persisted, so this is not a genuine failure.</summary>
    private sealed class FlowRetryScheduledSignal(FlowExecutionResult result) : Exception
    {
        public FlowExecutionResult Result { get; } = result;
    }
}
