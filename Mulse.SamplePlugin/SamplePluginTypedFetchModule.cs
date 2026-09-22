using Mulse.Modules;

namespace Mulse.SamplePlugin;

/// <summary>
/// Demonstrates <see cref="TypedFetchModule{TOut}"/>: a fetch module that acquires already-structured catalog
/// items from an in-memory stub (standing in for an API/database call) and hands them off as strongly-typed
/// records, with no manual JSON payload construction in the module's own code.
/// </summary>
public sealed class SamplePluginTypedFetchModule(TimeProvider timeProvider) : TypedFetchModule<SamplePluginTypedFetchModule.SampleCatalogItem>
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        [ModuleProtocol.Plugin],
        [ModuleCapability.Ingestion]);

    public override ModuleDescriptor Descriptor { get; } = new(
        "sample-plugin-typed-fetch",
        "Sample plugin typed fetch",
        ModuleKind.Fetch,
        "Demonstrates strongly-typed module authoring: fetches a small in-memory catalog directly as SampleCatalogItem records with no manual JSON payload construction.",
        recommendation: Recommendation);

    protected override Task<IReadOnlyList<SampleCatalogItem>> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<SampleCatalogItem> items =
        [
            new SampleCatalogItem("sku-001", "Widget", 9.99m),
            new SampleCatalogItem("sku-002", "Gadget", 19.99m),
            new SampleCatalogItem("sku-003", "Gizmo", 29.99m)
        ];

        return Task.FromResult(items);
    }

    protected override string BuildPayloadName(SampleCatalogItem item, int index)
        => $"{item.Id}.json";

    protected override IReadOnlyDictionary<string, string> BuildMetadata(SampleCatalogItem item, int index)
        => new Dictionary<string, string>
        {
            ["fetchedBy"] = Descriptor.Id,
            ["fetchedAtUtc"] = timeProvider.GetUtcNow().ToString("O")
        };

    /// <summary>The strongly-typed shape this module produces for each fetched catalog item.</summary>
    public sealed record SampleCatalogItem(string Id, string Name, decimal Price);
}
