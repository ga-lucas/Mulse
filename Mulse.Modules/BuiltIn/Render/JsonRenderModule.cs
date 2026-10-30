using System.Text.Json;

namespace Mulse.Modules.BuiltIn.Render;

public sealed class JsonRenderModule : IRenderModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("indented", "Indented", "Write indented JSON when enabled.", false, ModuleSettingInputKind.Boolean, "false"),
        new("combinePayloads", "Combine into one JSON array", "Emit one combined.json payload for the entire input batch. Objects/scalars become array items; top-level arrays contribute their items in order. Does not join rows by key. Empty input emits no file.", false, ModuleSettingInputKind.Boolean, "false")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        capabilities: [ModuleCapability.Serialization]);

    public ModuleDescriptor Descriptor { get; } = new(
        "json-render",
        "JSON render",
        ModuleKind.Render,
        "Serializes the working payload as JSON for downstream delivery modules.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var indented = ModuleSettingReader.GetBoolean(step.Settings, "indented", defaultValue: false, Descriptor.Id);
        var combinePayloads = ModuleSettingReader.GetBoolean(step.Settings, "combinePayloads", defaultValue: false, Descriptor.Id);
        if (combinePayloads)
        {
            return Task.FromResult(RenderCombined(batch, indented, cancellationToken));
        }

        var renderedPayloads = batch.Payloads.Select(payload =>
        {
            using var document = JsonDocument.Parse(payload.Content);
            var renderedContent = JsonSerializer.SerializeToUtf8Bytes(document.RootElement, new JsonSerializerOptions { WriteIndented = indented });
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id,
                ["renderedFormat"] = "Json"
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".json"),
                BinaryData.FromBytes(renderedContent),
                "application/json",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(renderedPayloads));
    }

    private IntegrationBatch RenderCombined(IntegrationBatch batch, bool indented, CancellationToken cancellationToken)
    {
        if (batch.Count == 0)
        {
            return IntegrationBatch.Empty;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented }))
        {
            writer.WriteStartArray();
            foreach (var payload in batch.Payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var document = JsonDocument.Parse(payload.Content);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        item.WriteTo(writer);
                    }
                }
                else
                {
                    document.RootElement.WriteTo(writer);
                }
            }
            writer.WriteEndArray();
        }

        var metadata = batch.Payloads[0].Metadata
            .Where(entry => batch.Payloads.All(payload => payload.Metadata.TryGetValue(entry.Key, out var value)
                && string.Equals(entry.Value, value, StringComparison.Ordinal)))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        metadata["renderedBy"] = Descriptor.Id;
        metadata["renderedFormat"] = "Json";
        return new IntegrationBatch([new IntegrationPayload("combined.json", BinaryData.FromBytes(stream.ToArray()), "application/json", metadata)]);
    }
}
