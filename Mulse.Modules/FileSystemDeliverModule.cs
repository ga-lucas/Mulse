using Microsoft.Extensions.Logging;

namespace Mulse.Modules;

public sealed class FileSystemDeliverModule(ILogger<FileSystemDeliverModule> logger, TimeProvider timeProvider) : IDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Path", "The local directory where rendered payloads should be written.", true),
        new("extension", "Extension override", "Optional file extension override such as .json or .xml.", false, ModuleSettingInputKind.Text)
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.FileSystem],
        [ModuleCapability.Storage, ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "file-system-deliver",
        "File system deliver",
        ModuleKind.Deliver,
        "Writes rendered payloads to a target directory for archiving or downstream pickup.",
        SettingDescriptors,
        Recommendation);

    public async Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(ModuleSettingReader.GetRequired(step.Settings, "path", Descriptor.Id));
        var configuredExtension = ModuleSettingReader.GetOptional(step.Settings, "extension");
        Directory.CreateDirectory(directory);

        for (var index = 0; index < batch.Payloads.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = batch.Payloads[index];
            var extension = string.IsNullOrWhiteSpace(configuredExtension)
                ? (Path.GetExtension(payload.Name) is { Length: > 0 } detectedExtension ? detectedExtension : ".bin")
                : configuredExtension;
            var fileName = Path.GetFileNameWithoutExtension(payload.Name);
            var timestamp = timeProvider.GetUtcNow().ToString("yyyyMMddHHmmssfff");
            var targetPath = Path.Combine(directory, $"{fileName}-{context.ExecutionId}-{timestamp}-{index}{extension}");

            await File.WriteAllBytesAsync(targetPath, payload.Content.ToArray(), cancellationToken).ConfigureAwait(false);

            logger.LogInformation(
                "Delivered payload {PayloadName} for flow {FlowId} execution {ExecutionId} to {TargetPath} using module {ModuleId}",
                payload.Name,
                context.FlowId,
                context.ExecutionId,
                targetPath,
                Descriptor.Id);
        }
    }
}
