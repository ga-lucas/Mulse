using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Modules.Sdk;

namespace Mulse.Billing;

public sealed class BillingFilenameAugmentModule : IOrchestrationAugmentModule
{
    public ModuleDescriptor Descriptor { get; } = new("billing-filename-augment", "Expand Billing target filename", ModuleKind.OrchestrationAugment,
        "Expands a literal or exact metadata template into explicit output metadata. Keeps payload body and name unchanged; does not route or deliver. Native durable execution journals the returned result.",
        [
            new("source", "Template source", "Read a literal template or an exact metadata key.", true, ModuleSettingInputKind.Select, "Metadata", [new("Metadata", "Metadata field"), new("Literal", "Literal template")]),
            new("template", "Template / source metadata key", "For Metadata choose the field populated by a preceding promotion, for example billing.TargetLocation.", true),
            new("outputKey", "Output metadata key", "Explicit target field; the original body and name are unchanged. Destination modules must explicitly opt in to this value.", true, ModuleSettingInputKind.Text, "billing.ExpandedTargetLocation"),
            .. BillingFilename.Settings
        ]);

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = ModuleSettingReader.GetRequired(step.Settings, "source", Descriptor.Id);
        var template = ModuleSettingReader.GetRequired(step.Settings, "template", Descriptor.Id);
        var outputKey = ModuleSettingReader.GetRequired(step.Settings, "outputKey", Descriptor.Id);
        if (source is not ("Literal" or "Metadata") || outputKey.Length > 128)
            throw new ArgumentException("Billing filename requires Literal/Metadata source and an output metadata key of at most 128 characters.");
        var environment = BillingFilename.ReadEnvironment(step.Settings, Descriptor.Id);
        var outputs = new List<IntegrationPayload>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = source == "Literal" ? template : payload.Metadata.FirstOrDefault(pair => pair.Key == template).Value
                ?? throw new ArgumentException("Billing filename source metadata is missing its exact key.");
            var value = BillingFilename.Expand(text, context.StartedAt, environment.Zone, environment.Culture, Guid.NewGuid());
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) { [outputKey] = value };
            outputs.Add(payload with { Metadata = metadata });
        }
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new IntegrationBatch(outputs));
    }
}
