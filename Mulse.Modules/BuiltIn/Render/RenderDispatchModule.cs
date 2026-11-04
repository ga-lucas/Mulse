using Mulse.Modules.Mapping;

namespace Mulse.Modules.BuiltIn.Render;

public sealed class RenderDispatchModule(IRenderModuleInvoker invoker) : IRenderModule
{
    public ModuleDescriptor Descriptor { get; } = new(RenderDispatchDefinition.ModuleId, "Select map / renderer", ModuleKind.Render,
        "Selects allowlisted renderers using metadata. Values are case-sensitive; missing or unknown selections fail. Process each payload separately by default, or explicitly group the current batch by selection.",
        [
            new("metadataKey", "Selection metadata key", "Metadata field set by a preceding decision or promotion, for example MapName.", true),
            new("batchMode", "Renderer input grouping", "PerPayload preserves input order. PerSelection invokes once per selection value, in first-seen group order. This groups only the current run, not messages across executions; the renderer decides whether to combine outputs.", false, ModuleSettingInputKind.Select, "PerPayload",
                [new("PerPayload", "Each payload separately"), new("PerSelection", "Group current batch by selection")]),
            new("casesJson", "Renderer choices", "Use the visual choices editor. Every choice names a loaded renderer and its settings. Nested dispatch is not supported.", true, ModuleSettingInputKind.TextArea, "[]")
        ]);

    public async Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var metadataKey = ModuleSettingReader.GetRequired(step.Settings, "metadataKey", Descriptor.Id);
        var batchMode = ModuleSettingReader.GetOptional(step.Settings, "batchMode") ?? "PerPayload";
        if (batchMode is not ("PerPayload" or "PerSelection")) throw new ArgumentException($"Module '{Descriptor.Id}' batchMode must be PerPayload or PerSelection.");
        var cases = RenderDispatchDefinition.Read(ModuleSettingReader.GetRequired(step.Settings, "casesJson", Descriptor.Id))
            .ToDictionary(entry => entry.Value, StringComparer.Ordinal);
        var selected = new List<(IntegrationPayload Payload, RenderDispatchCase Choice)>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!payload.Metadata.TryGetValue(metadataKey, out var value) || !cases.TryGetValue(value, out var choice))
                throw new ArgumentException($"Module '{Descriptor.Id}' requires every payload to select a configured renderer via metadata '{metadataKey}'.");
            selected.Add((payload, choice));
        }
        var inputs = batchMode == "PerSelection"
            ? selected.GroupBy(entry => entry.Choice.Value, StringComparer.Ordinal)
                .Select(group => (Choice: group.First().Choice, Batch: new IntegrationBatch(group.Select(entry => entry.Payload).ToArray())))
            : selected.Select(entry => (entry.Choice, Batch: new IntegrationBatch([entry.Payload])));
        var output = new List<IntegrationPayload>();
        foreach (var (choice, input) in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = await invoker.RenderAsync(choice.Module, context, input, choice.Settings, cancellationToken).ConfigureAwait(false);
            output.AddRange(rendered.Payloads);
        }
        return new IntegrationBatch(output);
    }
}
