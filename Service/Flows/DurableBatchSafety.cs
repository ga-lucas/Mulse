using System.Text.Json;
using Mulse.Modules.BuiltIn.Augment;

namespace Service.Flows;

internal static class DurableBatchSafety
{
    public static void Validate(PipelineDefinition pipeline)
    {
        var collectors = pipeline.Augments.Where(step => IsCollector(step.Module)).ToArray();
        var odx = pipeline.Augments.Where(step => step.Module.Equals(OdxProgramAugmentModule.ModuleId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (odx.Length > 1 || odx.Length > 0 && (collectors.Length > 0 || pipeline.Augments.Any(step => step.Module.Equals("stateful-orchestration-augment", StringComparison.OrdinalIgnoreCase))))
            throw new ArgumentException("Use one durable ODX program per flow, without other stateful orchestration/collector nodes.");
        if (odx.Any(step => !string.IsNullOrWhiteSpace(step.Settings.GetValueOrDefault("conditionJson"))))
            throw new ArgumentException("Durable ODX execution cannot be conditional: source receipts must not span selected and skipped messages.");
        if (collectors.Length == 0) return;
        if (collectors.Length > 1 || pipeline.Augments.Any(step => step.Module.Equals("stateful-orchestration-augment", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Use one durable batch collector per flow, without other stateful orchestration nodes.");
        if (collectors[0].Settings.TryGetValue("conditionJson", out var condition) && !string.IsNullOrWhiteSpace(condition))
            throw new ArgumentException("Durable batch collection cannot be conditional: a source receipt can cover both selected and skipped messages. Separate the source runs/flows instead.");
    }

    public static void EnsureUnchanged(PipelineDefinition existing, PipelineDefinition replacement, IEnumerable<FlowOrchestrationCheckpoint> checkpoints)
    {
        if (!checkpoints.Any(checkpoint => (checkpoint.Batch is not null || checkpoint.Odx is not null) && checkpoint.FlowId.Equals(existing.Id, StringComparison.OrdinalIgnoreCase))) return;
        if (JsonSerializer.Serialize(FlowMappings.CopyPipeline(existing, existing.Id, false, null))
            != JsonSerializer.Serialize(FlowMappings.CopyPipeline(replacement, replacement.Id, false, null)))
            throw new InvalidOperationException($"Flow '{existing.Id}' has pending durable batches or ODX continuations. Only enabled/category changes are allowed until they complete; pending data must not be reinterpreted under an edited flow.");
    }

    public static void ValidateResolvedSchedule(PipelineDefinition pipeline)
    {
        foreach (var collector in pipeline.Augments.Where(step => IsCollector(step.Module)))
            _ = DurableBatchSchedule.Read(collector);
    }

    private static bool IsCollector(string module) => module.Equals(DurableBatchAugmentModule.ModuleId, StringComparison.OrdinalIgnoreCase);
}
