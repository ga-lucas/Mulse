using Mulse.Modules;

namespace Service.Execution;

public sealed partial class FlowRuntime
{
    /// <summary>
    /// Resumes a previously persisted <see cref="FlowRetryState"/>, either because its delay has elapsed or
    /// because the service is recovering pending work found on disk after a restart. Returns <c>null</c> (after
    /// deleting the stale retry entry) if the flow has since been deleted or disabled.
    /// </summary>
    public async Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retryState, CancellationToken cancellationToken)
    {
        PipelineDefinition pipeline;
        try
        {
            pipeline = GetConfiguredFlow(retryState.FlowId);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(
                "Abandoning pending retry for flow {FlowId} execution {ExecutionId} stage {StageId}: {Reason}",
                retryState.FlowId, retryState.ExecutionId, retryState.StageId, exception.Message);
            await flowRetryStateStore.DeleteAllForExecutionAsync(retryState.FlowId, retryState.ExecutionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var gate = _flowLocks.GetOrAdd(pipeline.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = new FlowExecutionContext(pipeline.Id, retryState.ExecutionId, retryState.ExecutionStartedAt);
            var committedAtomicDeliveries = new List<FlowRetryCommittedDelivery>(retryState.CommittedAtomicDeliveries);
            var inputBatch = RestoreBatch(retryState.InputPayloads);

            try
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
