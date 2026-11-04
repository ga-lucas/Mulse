using System.Xml.Linq;

namespace Mulse.Modules.Decisions;

public sealed class XmlRulesAugmentModule : IOrchestrationAugmentModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "xml-rules-augment", "XML field rules", ModuleKind.OrchestrationAugment,
        "Evaluates typed XML equality rules in one pass. No forward chaining or BizTalk agenda emulation. Conflicting assignments and unmatched facts fail explicitly.",
        [
            new("sourceId", "Input source", "Only evaluate payloads from this source; other sources pass through unchanged.", false),
            new("factMode", "Policy input / output", "XML mode edits a fact document. Metadata mode preserves the original body, reads condition fields from metadata and writes assigned fields back to metadata.", false, ModuleSettingInputKind.Select, "PayloadXml",
                [new("PayloadXml", "XML fact document"), new("Metadata", "Metadata facts; preserve message body")]),
            new("metadataPrefix", "Fact metadata prefix", "Metadata mode reads/writes prefix + field local name, for example rules.GroupID and rules.MapName. Blank uses unprefixed keys. Existing assigned keys are overwritten; namespace collisions fail.", false, ModuleSettingInputKind.Text, "rules."),
            new("policyJson", "XML rule policy", "Configure the policy with the visual XML rule editor. Imported policies are native proposals, not verified BizTalk equivalents.", true, ModuleSettingInputKind.TextArea)
        ],
        new ModuleRecommendationProfile([ModuleDataFormat.Xml, ModuleDataFormat.Json], capabilities: [ModuleCapability.Transformation, ModuleCapability.Decision]));

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var policy = XmlRuleRuntime.ReadPolicy(ModuleSettingReader.GetRequired(step.Settings, "policyJson", Descriptor.Id));
        var sourceId = ModuleSettingReader.GetOptional(step.Settings, "sourceId");
        var mode = ModuleSettingReader.GetOptional(step.Settings, "factMode") ?? "PayloadXml";
        if (mode is not ("PayloadXml" or "Metadata")) throw new ArgumentException("XML rule factMode must be PayloadXml or Metadata.");
        var prefix = step.Settings.GetValueOrDefault("metadataPrefix", "rules.");
        if (prefix.Length > 128) throw new ArgumentException("XML rule metadataPrefix exceeds 128 characters.");
        var output = new List<IntegrationPayload>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceId is not null && (!payload.Metadata.TryGetValue("sourceId", out var source) || !string.Equals(sourceId, source, StringComparison.OrdinalIgnoreCase)))
            {
                output.Add(payload);
                continue;
            }
            var facts = mode == "Metadata" ? MetadataFacts(policy, payload.Metadata, prefix, cancellationToken) : payload.GetText();
            var result = XmlRuleRuntime.Evaluate(policy, facts, cancellationToken);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase);
            if (mode == "Metadata")
                foreach (var assignment in result.Assignments) metadata[prefix + assignment.Name] = assignment.Value;
            metadata["xmlRulesPolicy"] = policy.Name;
            metadata["xmlRulesVersion"] = policy.Version;
            metadata["xmlRulesMatchedCount"] = result.MatchedRules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            output.Add(mode == "Metadata" ? payload with { Metadata = metadata }
                : new(payload.Name, BinaryData.FromString(result.OutputXml), "application/xml", metadata));
        }
        return Task.FromResult(new IntegrationBatch(output));
    }

    private static string MetadataFacts(XmlRulePolicy policy, IReadOnlyDictionary<string, string> metadata, string prefix, CancellationToken ct)
    {
        var rules = policy.Rules.Where(rule => rule.Active && rule.ConstantCondition).ToArray();
        var conditions = rules.SelectMany(rule => rule.Conditions).Select(field => XName.Get(field.Name, field.Namespace)).ToHashSet();
        var fields = rules.SelectMany(rule => rule.Conditions.Concat(rule.Assignments)).Select(field => XName.Get(field.Name, field.Namespace)).Distinct().ToArray();
        if (fields.Length > 4096 || fields.Select(field => field.LocalName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != fields.Length)
            throw new ArgumentException("Metadata rule facts require at most 4096 fields with distinct local names (case-insensitive), including across namespaces.");
        var root = new XElement(XName.Get(policy.RootName, policy.RootNamespace));
        var size = 0;
        foreach (var field in fields)
        {
            ct.ThrowIfCancellationRequested();
            var key = prefix + field.LocalName;
            if (!metadata.TryGetValue(key, out var value))
            {
                if (conditions.Contains(field)) throw new ArgumentException($"XML rule condition requires metadata '{key}'.");
                value = "";
            }
            size += field.LocalName.Length + field.NamespaceName.Length + value.Length;
            if (size > XmlRuleRuntime.MaxXmlCharacters) throw new ArgumentException("XML rule metadata facts exceed 1000000 characters.");
            root.Add(new XElement(field, value));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }
}
