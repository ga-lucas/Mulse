using System.Text.Json;

namespace Mulse.Modules.BuiltIn.Render;

public sealed class JsonRenderModule : IRenderModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("indented", "Indented", "Write indented JSON when enabled.", false, ModuleSettingInputKind.Boolean, "false")
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
}
