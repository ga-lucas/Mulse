using System.Text.Json.Serialization;

namespace Mulse.SamplePlugin;

// Keep JSON metadata with the collectible package, rather than global reflection-emission caches.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(SamplePluginTypedFetchModule.SampleCatalogItem))]
[JsonSerializable(typeof(SamplePluginTypedAugmentModule.OrderDocument))]
[JsonSerializable(typeof(SamplePluginTypedAugmentModule.OrderSummary))]
internal partial class SamplePluginJsonContext : JsonSerializerContext
{
}
