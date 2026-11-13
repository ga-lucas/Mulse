using Mulse.Modules.ControlFlow;

namespace Service.Execution;

public sealed partial class FlowRuntime
{
    private static bool IsNativeDurableStage(PipelineDefinition pipeline, string stageId)
        => stageId.StartsWith("augment:", StringComparison.Ordinal)
            && int.TryParse(stageId.AsSpan("augment:".Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
            && index >= 0 && index < pipeline.Augments.Count
            && IsNativeDurable(pipeline.Augments[index]);

    private static bool IsNativeDurable(ModuleStepDefinition step)
        => step.Module.Equals(NativeFlowDefinition.ModuleId, StringComparison.OrdinalIgnoreCase)
            && NativeFlowDefinition.Read(step.Settings.GetValueOrDefault("flowJson", "")).Durable;

    private FlowOrchestrationCheckpoint? FindDueNative(IReadOnlyList<FlowOrchestrationCheckpoint> checkpoints)
        => checkpoints.Where(checkpoint => checkpoint.Native is { RecoveryPending: false, DeadlineUtc: not null }
                && checkpoint.Native.DeadlineUtc <= timeProvider.GetUtcNow())
            .OrderBy(checkpoint => checkpoint.Native!.DeadlineUtc).ThenBy(checkpoint => checkpoint.CorrelationKey, StringComparer.OrdinalIgnoreCase).FirstOrDefault();

    public async Task<FlowExecutionResult> ReconcileNativeEffectAsync(string flowId, string instanceId, string expectedEffectId, string evidence,
        IntegrationBatch? output, CancellationToken cancellationToken)
    {
        var pipeline = GetConfiguredFlow(flowId);
        var gate = _flowLocks.GetOrAdd(pipeline.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            pipeline = GetConfiguredFlow(flowId);
            using var moduleUsage = moduleCatalog.PinFlowModules(pipeline);
            await ValidateConfirmationAsync(pipeline, cancellationToken).ConfigureAwait(false);
            var checkpoint = (await orchestrationStateStore.GetAllAsync(pipeline.Id, cancellationToken).ConfigureAwait(false))
                .SingleOrDefault(item => item.InstanceId.Equals(instanceId, StringComparison.Ordinal))
                ?? throw new KeyNotFoundException("The inspected native instance no longer exists.");
            var step = pipeline.Augments.SingleOrDefault(IsNativeDurable) ?? throw new InvalidOperationException("This flow has no native durable definition.");
            var definition = NativeFlowDefinition.Read(step.Settings.GetValueOrDefault("flowJson", ""));
            await NativeDurableRecovery.ReconcileCommittedAsync(checkpoint, definition, expectedEffectId, evidence, output,
                orchestrationStateStore, timeProvider, cancellationToken).ConfigureAwait(false);
            logger.LogWarning("Operator reconciled verified committed native effect {EffectId} for flow {FlowId}, instance {InstanceId}. Resuming without replaying that effect or fetching.", expectedEffectId, pipeline.Id, instanceId);
            var persisted = await orchestrationStateStore.GetAsync(pipeline.Id, checkpoint.CorrelationKey, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Reconciled native checkpoint could not be reloaded; no continuation was run.");
            return await ResumeNativeCoreAsync(pipeline, persisted, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private async Task<FlowExecutionResult> ResumeNativeCoreAsync(PipelineDefinition pipeline, FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var index = pipeline.Augments.FindIndex(IsNativeDurable);
        if (index < 0 || checkpoint.Native is not { } state || state.ModuleId != pipeline.Augments[index].Module || string.IsNullOrWhiteSpace(state.LastExecutionId))
            throw new InvalidOperationException("Due native continuation has no compatible definition/execution identity. No input was fetched; inspect its pinned definition.");
        var started = timeProvider.GetUtcNow();
        var context = new FlowExecutionContext(pipeline.Id, state.LastExecutionId, started)
        {
            PendingSourceReceipts = await orchestrationStateStore.GetPendingSourceReceiptsAsync(pipeline.Id, cancellationToken).ConfigureAwait(false)
        };
        context.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, checkpoint.Bookmark, true);
        logger.LogInformation("Resuming due native instance {InstanceId} for flow {FlowId} without fetching or replaying upstream steps.", checkpoint.InstanceId, pipeline.Id);
        try
        {
            return await RunWithSourceOutcomesAsync(pipeline, context,
                () => RunAugmentOnwardAsync(pipeline, context, started, index, IntegrationBatch.Empty, 0, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (FlowRetryScheduledSignal signal) { return signal.Result; }
    }
}
