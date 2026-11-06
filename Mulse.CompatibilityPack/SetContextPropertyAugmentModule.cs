using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Modules.Sdk;

namespace Mulse.CompatibilityPack;

/// <summary>
/// Native equivalent of a <c>SetProperty</c> pipeline component, which promoted a configured context property
/// (name, namespace, value) onto each message. The property becomes the exact metadata key <c>{namespace}name</c>.
/// Like the source, nothing is written when any of the three values is empty.
/// </summary>
public sealed class SetContextPropertyAugmentModule : IOrchestrationAugmentModule
{
    public ModuleDescriptor Descriptor { get; } = new("set-context-property-augment", "Set context property", ModuleKind.OrchestrationAugment,
        "Writes a configured value to the exact metadata key '{namespace}name', replacing a SetProperty pipeline component. Writes nothing when name, namespace or value is empty, matching the source. The body is unchanged.",
        [
            new("propertyName", "Property name", "Context property name, for example FileName.", false),
            new("propertyNamespace", "Property namespace", "Context property namespace, for example the MIME property namespace.", false),
            new("propertyValue", "Property value", "Literal value written to the property.", false)
        ]);

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var name = ModuleSettingReader.GetOptional(step.Settings, "propertyName");
        var ns = ModuleSettingReader.GetOptional(step.Settings, "propertyNamespace");
        var value = ModuleSettingReader.GetOptional(step.Settings, "propertyValue");
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(value))
        {
            return Task.FromResult(batch);
        }

        var key = "{" + ns + "}" + name;
        var outputs = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return payload with { Metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) { [key] = value } };
        }).ToArray();
        return Task.FromResult(new IntegrationBatch(outputs));
    }
}
