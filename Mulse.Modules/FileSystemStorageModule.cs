using Microsoft.Extensions.Logging;

namespace Mulse.Modules;

public sealed class FileSystemStorageModule(ILogger<FileSystemStorageModule> logger, TimeProvider timeProvider) : IOutputModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "file-system-storage",
        "File system output",
        ModuleKind.Output,
        "Persists transformed payloads to a target directory for archiving or downstream pickup.");

    public async Task WriteAsync(
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
                "Stored payload {PayloadName} for flow {FlowId} execution {ExecutionId} at {TargetPath} using module {ModuleId}",
                payload.Name,
                context.FlowId,
                context.ExecutionId,
                targetPath,
                Descriptor.Id);
        }
    }
}
