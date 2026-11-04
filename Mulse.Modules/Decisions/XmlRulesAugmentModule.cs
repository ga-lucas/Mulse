namespace Mulse.Modules.Decisions;

public sealed class XmlRulesAugmentModule : IOrchestrationAugmentModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "xml-rules-augment", "XML field rules", ModuleKind.OrchestrationAugment,
        "Evaluates typed XML equality rules in one pass. No forward chaining or BizTalk agenda emulation. Conflicting assignments and unmatched facts fail explicitly.",
        [
            new("sourceId", "Input source", "Only evaluate payloads from this source; other sources pass through unchanged.", false),
            new("policyJson", "XML rule policy", "Configure the policy with the visual XML rule editor. Imported policies are native proposals, not verified BizTalk equivalents.", true, ModuleSettingInputKind.TextArea)
        ],
        new ModuleRecommendationProfile([ModuleDataFormat.Xml], capabilities: [ModuleCapability.Transformation, ModuleCapability.Decision]));

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var policy = XmlRuleRuntime.ReadPolicy(ModuleSettingReader.GetRequired(step.Settings, "policyJson", Descriptor.Id));
        var sourceId = ModuleSettingReader.GetOptional(step.Settings, "sourceId");
        var output = new List<IntegrationPayload>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceId is not null && (!payload.Metadata.TryGetValue("sourceId", out var source) || !string.Equals(sourceId, source, StringComparison.OrdinalIgnoreCase)))
            {
                output.Add(payload);
                continue;
            }
            var result = XmlRuleRuntime.Evaluate(policy, payload.GetText(), cancellationToken);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["xmlRulesPolicy"] = policy.Name,
                ["xmlRulesVersion"] = policy.Version,
                ["xmlRulesMatchedCount"] = result.MatchedRules.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            output.Add(new(payload.Name, BinaryData.FromString(result.OutputXml), "application/xml", metadata));
        }
        return Task.FromResult(new IntegrationBatch(output));
    }
}
