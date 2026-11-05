namespace Service.Execution;

public sealed partial class FlowRuntime
{
    public async Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flowId, CancellationToken cancellationToken)
    {
        var pipeline = GetConfiguredFlow(flowId);
        var gate = _flowLocks.GetOrAdd(pipeline.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pipeline = GetConfiguredFlow(flowId);
            using var moduleUsage = moduleCatalog.PinFlowModules(pipeline);
            if ((await flowRetryStateStore.GetAllAsync(cancellationToken).ConfigureAwait(false))
                .Any(retry => retry.FlowId.Equals(pipeline.Id, StringComparison.OrdinalIgnoreCase))) return null;
            var checkpoints = await orchestrationStateStore.GetAllAsync(pipeline.Id, cancellationToken).ConfigureAwait(false);
            if (checkpoints.Any(checkpoint => checkpoint.Batch?.Ready == true || checkpoint.Odx?.RecoveryPending == true || checkpoint.Native?.RecoveryPending == true)) return null;
            var due = FindDueBatch(checkpoints);
            if (due is not null) return await ReleaseBatchCoreAsync(pipeline, due, cancellationToken).ConfigureAwait(false);
            var odx = FindDueOdx(checkpoints);
            if (odx is not null) return await ResumeOdxCoreAsync(pipeline, odx, cancellationToken).ConfigureAwait(false);
            var native = FindDueNative(checkpoints);
            return native is null ? null : await ResumeNativeCoreAsync(pipeline, native, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public async Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flowId, string instanceId, int expectedPayloadCount, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(instanceId) || expectedPayloadCount is < 1 or > 10000)
            throw new ArgumentException("Confirm a batch instance and its inspected payload count (1-10000).");
        var pipeline = GetConfiguredFlow(flowId);
        var gate = _flowLocks.GetOrAdd(pipeline.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pipeline = GetConfiguredFlow(flowId);
            using var moduleUsage = moduleCatalog.PinFlowModules(pipeline);
            if ((await flowRetryStateStore.GetAllAsync(cancellationToken).ConfigureAwait(false)).Any(retry => retry.FlowId.Equals(pipeline.Id, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("This flow has pending retry/source work. Resolve it before releasing another batch.");
            var checkpoints = await orchestrationStateStore.GetAllAsync(pipeline.Id, cancellationToken).ConfigureAwait(false);
            if (checkpoints.Any(checkpoint => checkpoint.Batch?.Ready == true))
                throw new InvalidOperationException("This flow has already-released batch data awaiting completion/recovery. Manual release cannot replay it.");
            var checkpoint = checkpoints.FirstOrDefault(candidate => candidate.InstanceId == instanceId && candidate.Batch is not null)
                ?? throw new KeyNotFoundException("The selected collecting batch no longer exists. Refresh checkpoints before confirming release.");
            if (checkpoint.Payloads.Count != expectedPayloadCount)
                throw new InvalidOperationException("The batch changed since inspection. Refresh checkpoints and confirm the new payload count before releasing.");
            var collector = pipeline.Augments.FirstOrDefault(step => step.Module.Equals("durable-batch-augment", StringComparison.OrdinalIgnoreCase));
            if (collector is null || string.IsNullOrWhiteSpace(checkpoint.Batch!.LastExecutionId))
                throw new InvalidOperationException("The saved batch has no compatible collector/execution identity; no release was attempted.");
            Mulse.Modules.BuiltIn.Augment.DurableBatchAugmentModule.ValidateCheckpointSettings(checkpoint, collector);
            var state = checkpoint.Batch;
            var prepared = new FlowOrchestrationCheckpoint
            {
                FlowId = checkpoint.FlowId, CorrelationKey = checkpoint.CorrelationKey, InstanceId = checkpoint.InstanceId, Bookmark = checkpoint.Bookmark,
                UpdatedAt = timeProvider.GetUtcNow(), Payloads = checkpoint.Payloads, SourceReceipts = checkpoint.SourceReceipts, StateValues = checkpoint.StateValues,
                Batch = new()
                {
                    CollectorId = state.CollectorId, MetadataKey = state.MetadataKey, TargetCount = state.TargetCount,
                    LastExecutionId = state.LastExecutionId, ScheduleKey = state.ScheduleKey, DeadlineUtc = state.DeadlineUtc,
                    OriginalBatchTime = state.OriginalBatchTime, PolicyBatchInterval = state.PolicyBatchInterval, Ready = true
                }
            };
            // Commit release intent before delivery. A crash/ambiguous write freezes this instance instead of making a second click replay it.
            await orchestrationStateStore.UpsertAsync(prepared, cancellationToken).ConfigureAwait(false);
            if (logger.IsEnabled(LogLevel.Information))
                logger.LogInformation("Operator confirmed early release of flow {FlowId} batch {InstanceId} with {Count} payloads.", pipeline.Id, instanceId, expectedPayloadCount);
            return await ReleaseBatchCoreAsync(pipeline, prepared, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private FlowOrchestrationCheckpoint? FindDueBatch(IReadOnlyList<FlowOrchestrationCheckpoint> checkpoints)
        => checkpoints.Where(checkpoint => checkpoint.Batch is { Ready: false, DeadlineUtc: not null }
                && checkpoint.Batch.DeadlineUtc <= timeProvider.GetUtcNow())
            .OrderBy(checkpoint => checkpoint.Batch!.DeadlineUtc).ThenBy(checkpoint => checkpoint.CorrelationKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    private async Task<FlowExecutionResult> ReleaseBatchCoreAsync(PipelineDefinition pipeline, FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var index = pipeline.Augments.FindIndex(step => step.Module.Equals("durable-batch-augment", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || checkpoint.Batch is not { } batch || string.IsNullOrWhiteSpace(batch.LastExecutionId) || checkpoint.Payloads.Count == 0)
            throw new InvalidOperationException("Due batch has no compatible collector, execution identity or input snapshots. No new data was fetched; inspect the saved checkpoint.");
        var started = timeProvider.GetUtcNow();
        var context = new FlowExecutionContext(pipeline.Id, batch.LastExecutionId, started)
        {
            PendingSourceReceipts = await orchestrationStateStore.GetPendingSourceReceiptsAsync(pipeline.Id, cancellationToken).ConfigureAwait(false)
        };
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Releasing due batch {InstanceId} for flow {FlowId} at persisted deadline {Deadline}; no source fetch or upstream mapping is invoked.",
                checkpoint.InstanceId, pipeline.Id, batch.DeadlineUtc);
        try
        {
            return await RunWithSourceOutcomesAsync(pipeline, context,
                () => RunAugmentOnwardAsync(pipeline, context, started, index, new IntegrationBatch(checkpoint.Payloads.Select(snapshot => snapshot.Restore()).ToArray()), 0, cancellationToken),
                cancellationToken).ConfigureAwait(false);
        }
        catch (FlowRetryScheduledSignal signal) { return signal.Result; }
    }
}
