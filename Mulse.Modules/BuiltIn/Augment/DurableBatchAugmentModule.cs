using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Mulse.Modules.BuiltIn.Augment;

public sealed class DurableBatchAugmentModule(IFlowOrchestrationStateStore store, TimeProvider timeProvider,
    ILogger<DurableBatchAugmentModule> logger) : IOrchestrationAugmentModule
{
    public const string ModuleId = "durable-batch-augment";
    private const int MaxPayloads = 10000;
    private const int MaxSnapshotCharacters = 24000000;

    public ModuleDescriptor Descriptor { get; } = new(ModuleId, "Collect correlated batch", ModuleKind.OrchestrationAugment,
        "Persists messages across runs until the minimum payload count is reached. Each run must contain one correlation value. Sources remain pending until delivery. No timer or automatic replay of failed ready batches.",
        [
            new("collectorId", "Collector name", "Stable name for this checkpoint. Use only one durable collector per flow; do not combine it with stateful orchestration nodes.", true),
            new("metadataKey", "Correlation metadata key", "All incoming payloads must have the same nonblank value. Correlation values are case-insensitive, like orchestration checkpoint keys. Promote the client/batch group before this step.", true),
            new("targetCount", "Minimum payload count", "Release the entire accumulated batch at or above this count (1-10000). An incoming run is never split; overflow beyond 10000 fails. No deadline or timed flush is implied.", true, ModuleSettingInputKind.Number, "100")
        ]);

    public async Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.PendingCheckpointCompletion is not null || context.OrchestrationInstanceId is not null)
            throw new InvalidOperationException("Durable batch collection cannot share an execution with another orchestration checkpoint.");
        var collector = ModuleSettingReader.GetRequired(step.Settings, "collectorId", ModuleId);
        var key = ModuleSettingReader.GetRequired(step.Settings, "metadataKey", ModuleId);
        var rawCount = ModuleSettingReader.GetRequired(step.Settings, "targetCount", ModuleId);
        if (collector.Length > 256 || key.Length > 256 || !int.TryParse(rawCount, NumberStyles.None, CultureInfo.InvariantCulture, out var target) || target is < 1 or > MaxPayloads)
            throw new ArgumentException("Durable batch requires collector/key names of at most 256 characters and targetCount from 1 to 10000.");
        if (batch.Count == 0) return batch;
        var correlations = batch.Payloads.Select(payload => payload.Metadata.TryGetValue(key, out var value) ? value : null)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (correlations.Length != 1 || string.IsNullOrWhiteSpace(correlations[0]) || correlations[0]!.Length > 1024)
            throw new ArgumentException("Every input in a durable batch run must have one identical nonblank correlation value of at most 1024 characters. Split clients into separate source runs/flows before collecting.");
        var correlation = correlations[0]!;
        var existing = await store.GetAsync(context.FlowId, correlation, cancellationToken).ConfigureAwait(false);
        if (existing is not null && (existing.Batch is not { } state || state.CollectorId != collector || state.MetadataKey != key || state.TargetCount != target))
            throw new InvalidOperationException("The persisted batch belongs to another collector/configuration. Restore the original configuration; pending data has not been replaced.");
        if (existing?.Batch is { Ready: true } ready && ready.LastExecutionId != context.ExecutionId)
            throw new InvalidOperationException("This batch is already released or requires failure recovery. Resume its pending retry; new input cannot be appended or deliveries replayed automatically.");
        var sameExecution = existing?.Batch?.LastExecutionId == context.ExecutionId;
        if (!sameExecution && ((existing?.Payloads.Count ?? 0) + (long)batch.Count > MaxPayloads
            || batch.Payloads.Sum(payload => (long)payload.Content.ToMemory().Length) > 16000000))
            throw new ArgumentException("Durable batch exceeds 10000 payloads or 16000000 incoming content bytes; the pending batch was not changed.");
        var payloads = sameExecution ? existing!.Payloads : (existing?.Payloads ?? []).Concat(batch.Payloads.Select(FlowOrchestrationPayloadSnapshot.Capture)).ToList();
        var receipts = (existing?.SourceReceipts ?? []).Concat(context.SourceReceipts).DistinctBy(receipt => receipt.Id).ToList();
        var checkpoint = new FlowOrchestrationCheckpoint
        {
            FlowId = context.FlowId, CorrelationKey = existing?.CorrelationKey ?? correlation,
            InstanceId = existing?.InstanceId ?? Guid.CreateVersion7().ToString(), Bookmark = collector,
            UpdatedAt = timeProvider.GetUtcNow(), Payloads = payloads, SourceReceipts = receipts,
            Batch = new() { CollectorId = collector, MetadataKey = key, TargetCount = target, LastExecutionId = context.ExecutionId, Ready = payloads.Count >= target }
        };
        if (JsonSerializer.Serialize(checkpoint).Length > MaxSnapshotCharacters)
            throw new ArgumentException("Durable batch checkpoint exceeds 24000000 JSON characters; the pending batch was not changed.");
        await store.UpsertAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        foreach (var receipt in receipts) context.RegisterSourceReceipt(receipt);
        context.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, collector, existing is not null);
        if (!checkpoint.Batch.Ready)
        {
            context.Suspend($"Batch '{collector}' has {payloads.Count}/{target} payloads; waiting for correlation '{checkpoint.CorrelationKey}'.");
            logger.LogInformation("Flow {FlowId} collected batch {InstanceId}: {Count}/{Target}; sources remain pending.", context.FlowId, checkpoint.InstanceId, payloads.Count, target);
            return IntegrationBatch.Empty;
        }
        context.MarkCheckpointForCompletion(checkpoint.FlowId, checkpoint.CorrelationKey);
        logger.LogInformation("Flow {FlowId} released durable batch {InstanceId} with {Count} payloads. Checkpoint remains until successful completion.", context.FlowId, checkpoint.InstanceId, payloads.Count);
        return new IntegrationBatch(payloads.Select(snapshot =>
        {
            var restored = snapshot.Restore();
            var metadata = new Dictionary<string, string>(restored.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["batch.instanceId"] = checkpoint.InstanceId, ["batch.correlationKey"] = checkpoint.CorrelationKey,
                ["batch.count"] = payloads.Count.ToString(CultureInfo.InvariantCulture)
            };
            return restored with { Metadata = metadata };
        }).ToArray());
    }
}
