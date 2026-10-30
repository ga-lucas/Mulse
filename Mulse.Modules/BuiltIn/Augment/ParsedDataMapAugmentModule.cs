using Mulse.Modules.Mapping;

namespace Mulse.Modules.BuiltIn.Augment;

public sealed class ParsedDataMapAugmentModule : IOrchestrationAugmentModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "parsed-data-map-augment", "Map parsed data", ModuleKind.OrchestrationAugment,
        "Maps parsed records into typed JSON objects before rendering. Works with JSON-producing parsers, XML, and parsed text; no second source is required.",
        [
            new("sourceId", "Input source", "Only map payloads with this sourceId; leave blank to map the current working batch. Other sources pass through unchanged.", false),
            new("inputFormat", "Parsed format", "Auto reads JSON or XML. Choose Text for a parser that emits plain text.", false, ModuleSettingInputKind.Select, "Auto",
                [new("Auto", "JSON / XML"), new("Json", "JSON"), new("Xml", "XML"), new("Text", "Text")]),
            new("mappingJson", "Field mappings", "Configure fields in the Mapping workspace. Paths use JSON Pointer syntax; output is an array of mapped objects per input payload.", true, ModuleSettingInputKind.TextArea)
        ],
        new ModuleRecommendationProfile([ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Text], capabilities: [ModuleCapability.Mapping, ModuleCapability.Transformation]));

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var mapping = ModuleSettingReader.GetRequired(step.Settings, "mappingJson", Descriptor.Id);
        ParsedDataMapping.ReadMap(mapping);
        var sourceId = ModuleSettingReader.GetOptional(step.Settings, "sourceId");
        var format = ModuleSettingReader.GetOptional(step.Settings, "inputFormat") ?? "Auto";
        var outputs = new List<IntegrationPayload>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (sourceId is not null && (!payload.Metadata.TryGetValue("sourceId", out var source) || !string.Equals(sourceId, source, StringComparison.OrdinalIgnoreCase)))
            {
                outputs.Add(payload);
                continue;
            }
            var mapped = ParsedDataMapping.Map(mapping, payload.GetText(), format, cancellationToken);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase) { ["mappedBy"] = Descriptor.Id };
            outputs.Add(new(Path.ChangeExtension(payload.Name, ".json"), BinaryData.FromString(mapped), "application/json", metadata));
        }
        return Task.FromResult(new IntegrationBatch(outputs));
    }
}
