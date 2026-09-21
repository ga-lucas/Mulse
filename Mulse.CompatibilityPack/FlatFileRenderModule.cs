using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class FlatFileRenderModule : IRenderModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("delimiter", "Delimiter", "The flat-file field delimiter.", false, ModuleSettingInputKind.Select, "Comma",
        [
            new ModuleSettingOption("Comma", "Comma"),
            new ModuleSettingOption("Pipe", "Pipe"),
            new ModuleSettingOption("Semicolon", "Semicolon"),
            new ModuleSettingOption("Tab", "Tab"),
            new ModuleSettingOption("Custom", "Custom")
        ]),
        new("customDelimiter", "Custom delimiter", "The delimiter character used when delimiter is set to Custom.", false),
        new("quoteCharacter", "Quote character", "The quote character used for escaped fields.", false, ModuleSettingInputKind.Text, "\""),
        new("includeHeader", "Include header row", "Writes a header row from record property names when enabled.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Serialization, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "flat-file-render",
        "Flat-file render",
        ModuleKind.Render,
        "Renders normalized JSON record sets into delimiter-based flat-file text for outbound delivery.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var delimiter = FlatFileCodec.ResolveDelimiter(step.Settings, Descriptor.Id);
        var quoteCharacter = FlatFileCodec.ResolveQuoteCharacter(step.Settings);
        var includeHeader = ModuleSettings.GetBoolean(step.Settings, "includeHeader", defaultValue: true, Descriptor.Id);

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = CompatibilityPayloadNavigator.ParseJson(payload, Descriptor.Id);
            var rendered = FlatFileCodec.Render(json, delimiter, quoteCharacter, includeHeader);
            var extension = delimiter == ',' ? ".csv" : ".txt";
            var contentType = delimiter == ',' ? "text/csv" : "text/plain";
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id,
                ["renderedFormat"] = "FlatFile",
                ["delimiter"] = delimiter.ToString()
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, extension),
                BinaryData.FromString(rendered),
                contentType,
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(payloads));
    }
}
