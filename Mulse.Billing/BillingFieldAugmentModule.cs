using System.Xml.Linq;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Mapping;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Modules.Sdk;

namespace Mulse.Billing;

public sealed class BillingFieldAugmentModule : IOrchestrationAugmentModule
{
    public const string BillingNamespace = "http://Shs.SpaceTrax2.Interfaces.Core.Schemas.Services.Billing.CommonBilling";
    private static readonly IReadOnlyDictionary<string, (string Section, string Element)> Fields = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
    {
        ["CaseID"] = ("Details", "CaseId"), ["ScanListID"] = ("Details", "ScanListId"), ["EncounterID"] = ("Details", "EncounterId"),
        ["UpdateMethod"] = ("Details", "UpdateMethod"), ["CaseNumber"] = ("Details", "CaseNumber"),
        ["DepartmentID"] = ("ConfigurationInfo", "DepartmentID"), ["DepartmentName"] = ("ConfigurationInfo", "DepartmentName"),
        ["TransportType"] = ("ConfigurationInfo", "TransportType"), ["TargetLocation"] = ("ConfigurationInfo", "TargetLocation"),
        ["BatchTime"] = ("ConfigurationInfo", "BatchTime"), ["BatchGroup"] = ("ConfigurationInfo", "BatchGroup"),
        ["IsBatch"] = ("ConfigurationInfo", "IsBatch"), ["GroupID"] = ("ConfigurationInfo", "GroupID")
    };
    public ModuleDescriptor Descriptor { get; } = new("billing-field-augment", "Read CommonBilling field", ModuleKind.OrchestrationAugment,
        "Reads one explicit, nonsensitive field from the supplied CommonBilling schema into native text metadata. Does not emulate promoted-property bags or validate the complete business schema. Credentials are deliberately excluded.",
        [
            new("field", "CommonBilling field", "Exact source-schema field. Requires one existing simple element under its exact unqualified section; duplicate/missing/complex values fail.", true, ModuleSettingInputKind.Select, "TargetLocation", Fields.Keys.Select(name => new ModuleSettingOption(name, name)).ToArray()),
            new("outputKey", "Output metadata key", "Explicit native metadata identity. Use this same key in native conditions, correlation keys or renderer selection. No aliases are inferred.", true, ModuleSettingInputKind.Text, "billing.TargetLocation")
        ]);

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = ModuleSettingReader.GetRequired(step.Settings, "field", Descriptor.Id);
        var key = ModuleSettingReader.GetRequired(step.Settings, "outputKey", Descriptor.Id);
        if (!Fields.TryGetValue(field, out var selection) || key.Length > 128)
            throw new ArgumentException("Select a supported nonsensitive CommonBilling field and an output metadata key of at most 128 characters.");
        var output = new List<IntegrationPayload>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = XmlMappingSchemas.ReadXml(payload.GetText(), preserveWhitespace: true);
            if (document.Root?.Name != XName.Get("Billing", BillingNamespace))
                throw new ArgumentException("CommonBilling input must have the exact Billing root name and namespace.");
            var sections = document.Root.Elements(selection.Section).ToArray();
            var elements = sections.SelectMany(section => section.Elements(selection.Element)).ToArray();
            if (sections.Length != 1 || elements.Length != 1 || elements[0].HasElements || elements[0].Attributes().Any(attribute => !attribute.IsNamespaceDeclaration)
                || elements[0].Value.Length > 4096)
                throw new ArgumentException("CommonBilling field requires one existing simple element of at most 4096 characters; no missing or ambiguous value was inferred.");
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) { [key] = elements[0].Value };
            output.Add(payload with { Metadata = metadata });
        }
        return Task.FromResult(new IntegrationBatch(output));
    }
}
