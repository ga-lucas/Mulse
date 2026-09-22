using Mulse.Modules;

namespace Mulse.SamplePlugin;

/// <summary>
/// Demonstrates <see cref="TypedParseModule{TOut}"/>: a parse module that interprets a simple pipe-delimited
/// text format (<c>"id|name|price"</c> lines) into the same <see cref="SamplePluginTypedFetchModule.SampleCatalogItem"/>
/// shape the typed fetch sample produces directly, showing two different ways to arrive at the same typed shape.
/// </summary>
public sealed class SamplePluginTypedParseModule : TypedParseModule<SamplePluginTypedFetchModule.SampleCatalogItem>
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Text],
        [ModuleProtocol.Plugin],
        [ModuleCapability.Transformation]);

    public override ModuleDescriptor Descriptor { get; } = new(
        "sample-plugin-typed-parse",
        "Sample plugin typed parse",
        ModuleKind.Parse,
        "Demonstrates strongly-typed module authoring: interprets pipe-delimited 'id|name|price' text lines directly into SampleCatalogItem records with no manual JSON handling.",
        recommendation: Recommendation);

    protected override Task<SamplePluginTypedFetchModule.SampleCatalogItem?> ParseAsync(
        FlowExecutionContext context,
        IntegrationPayload sourcePayload,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var line = sourcePayload.GetText().Trim();
        if (line.Length == 0)
        {
            // Drop blank/whitespace-only payloads instead of erroring.
            return Task.FromResult<SamplePluginTypedFetchModule.SampleCatalogItem?>(null);
        }

        var parts = line.Split('|');
        if (parts.Length != 3 || !decimal.TryParse(parts[2], out var price))
        {
            throw new InvalidOperationException(
                $"Module '{Descriptor.Id}' requires payloads shaped like 'id|name|price'. Payload '{sourcePayload.Name}' was '{line}'.");
        }

        var item = new SamplePluginTypedFetchModule.SampleCatalogItem(parts[0], parts[1], price);
        return Task.FromResult<SamplePluginTypedFetchModule.SampleCatalogItem?>(item);
    }
}
