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
        "Explicitly projects one nonsensitive CommonBilling body field to custom text metadata or its declared source context-property key. Does not emulate MessageBox subscriptions or validate the complete business schema. Credentials are deliberately excluded.",
        [
            new("field", "CommonBilling field", "Exact source-schema field. Requires one existing simple element under its exact unqualified section; duplicate/missing/complex values fail.", true, ModuleSettingInputKind.Select, "TargetLocation", Fields.Keys.Select(name => new ModuleSettingOption(name, name)).ToArray()),
            new("outputIdentity", "Output identity", "Custom keeps the existing text-metadata behavior. SourceProperty explicitly writes the selected field's schema-declared {namespace}property identity; IsBatch is validated and normalized as XML Boolean. This overwrites that one key: do not reproject after intentional context changes.", false, ModuleSettingInputKind.Select, "Custom", [new("Custom", "Custom metadata key"), new("SourceProperty", "Declared Billing context property")]),
            new("outputKey", "Custom output metadata key", "Required in Custom mode. Ignored in SourceProperty mode, which derives the exact identity from the selected field's source schema, not its body element name.", false, ModuleSettingInputKind.Text, "billing.TargetLocation")
        ]);

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var field = ModuleSettingReader.GetRequired(step.Settings, "field", Descriptor.Id);
        var identity = step.Settings.TryGetValue("outputIdentity", out var configuredIdentity) ? configuredIdentity : "Custom";
        if (!Fields.TryGetValue(field, out var selection) || identity is not ("Custom" or "SourceProperty"))
            throw new ArgumentException("Select a supported nonsensitive CommonBilling field and Custom or SourceProperty output identity.");
        var key = identity == "Custom" ? ModuleSettingReader.GetRequired(step.Settings, "outputKey", Descriptor.Id)
            : "{http://Shs.SpaceTrax2.Interfaces.Core.Schemas.Properties." + (selection.Section == "Details" ? "BillingProperties" : "CoreProperties") + "}" + field;
        if (key.Length > 128) throw new ArgumentException("CommonBilling output metadata key exceeds 128 characters.");
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
            var value = elements[0].Value;
            if (identity == "SourceProperty" && field == "IsBatch")
            {
                try { value = System.Xml.XmlConvert.ToString(System.Xml.XmlConvert.ToBoolean(value.Trim(' ', '\t', '\r', '\n'))); }
                catch (FormatException exception) { throw new ArgumentException("CommonBilling IsBatch context projection requires an XML Boolean value.", exception); }
            }
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) { [key] = value };
            output.Add(payload with { Metadata = metadata });
        }
        return Task.FromResult(new IntegrationBatch(output));
    }
}
