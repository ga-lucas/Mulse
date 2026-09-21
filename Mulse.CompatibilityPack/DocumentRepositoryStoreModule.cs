using System.Text.Json;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class DocumentRepositoryStoreModule(TimeProvider timeProvider) : IDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Repository path", "The destination document repository root directory.", true),
        new("extension", "Extension override", "Optional outbound file extension override such as .xml or .txt.", false),
        new("writeMetadataSidecar", "Write metadata sidecar", "Writes a sibling .metadata.json file for each stored document when enabled.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.FileSystem],
        [ModuleCapability.Storage, ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "document-repository-store",
        "Document repository store",
        ModuleKind.Deliver,
        "Stores rendered payloads into a repository-style directory and optionally persists sidecar metadata for later pickup.",
        SettingDescriptors,
        Recommendation);

    public async Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var repositoryPath = Path.GetFullPath(ModuleSettings.GetRequired(step.Settings, "path", Descriptor.Id));
        var configuredExtension = ModuleSettings.GetOptional(step.Settings, "extension");
        var writeMetadataSidecar = ModuleSettings.GetBoolean(step.Settings, "writeMetadataSidecar", defaultValue: true, Descriptor.Id);
        Directory.CreateDirectory(repositoryPath);

        for (var index = 0; index < batch.Payloads.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = batch.Payloads[index];
            var extension = string.IsNullOrWhiteSpace(configuredExtension)
                ? (Path.GetExtension(payload.Name) is { Length: > 0 } detectedExtension ? detectedExtension : ".bin")
                : configuredExtension;
            var fileName = Path.GetFileNameWithoutExtension(payload.Name);
            var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff");
            var targetPath = Path.Combine(repositoryPath, $"{fileName}-{context.ExecutionId}-{timestamp}-{index}{extension}");

            await File.WriteAllBytesAsync(targetPath, payload.Content.ToArray(), cancellationToken).ConfigureAwait(false);
            if (writeMetadataSidecar)
            {
                var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in payload.Metadata)
                {
                    metadata[entry.Key] = entry.Value;
                }

                metadata["storedBy"] = Descriptor.Id;
                metadata["flowId"] = context.FlowId;
                metadata["executionId"] = context.ExecutionId;
                metadata["contentType"] = payload.ContentType;
                metadata["originalName"] = payload.Name;

                var metadataJson = JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(targetPath + ".metadata.json", metadataJson, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
