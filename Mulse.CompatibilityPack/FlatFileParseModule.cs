using System.Text.Json;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class FlatFileParseModule : IParseModule
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
        new("hasHeader", "Has header row", "Treats the first record as a header row when enabled.", false, ModuleSettingInputKind.Boolean, "true"),
        new("trimValues", "Trim values", "Trims field values during parsing when enabled.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Csv, ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Parsing, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "flat-file-parse",
        "Flat-file parse",
        ModuleKind.Parse,
        "Parses delimiter-based flat files into normalized JSON records for downstream augmentation and rendering.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> ParseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var delimiter = FlatFileCodec.ResolveDelimiter(step.Settings, Descriptor.Id);
        var quoteCharacter = FlatFileCodec.ResolveQuoteCharacter(step.Settings);
        var hasHeader = ModuleSettings.GetBoolean(step.Settings, "hasHeader", defaultValue: true, Descriptor.Id);
        var trimValues = ModuleSettings.GetBoolean(step.Settings, "trimValues", defaultValue: true, Descriptor.Id);

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var records = FlatFileCodec.ParseRecords(payload.GetText(), delimiter, quoteCharacter, trimValues);
            var jsonNode = FlatFileCodec.ToJson(records, hasHeader);
            var json = jsonNode.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["parsedBy"] = Descriptor.Id,
                ["parsedFormat"] = "FlatFile",
                ["recordCount"] = (hasHeader ? Math.Max(records.Count - 1, 0) : records.Count).ToString(),
                ["delimiter"] = delimiter.ToString()
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".json"),
                BinaryData.FromString(json),
                "application/json",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(payloads));
    }
}
