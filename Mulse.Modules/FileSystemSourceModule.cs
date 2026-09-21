namespace Mulse.Modules;

public sealed class FileSystemSourceModule : IInputModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "file-system",
        "File system input",
        ModuleKind.Input,
        "Reads files from a local or mounted directory so flows can ingest file-based integrations.");

    public async Task<IntegrationBatch> ReadAsync(
        FlowExecutionContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(ModuleSettingReader.GetRequired(step.Settings, "path", Descriptor.Id));
        var searchPattern = ModuleSettingReader.GetOptional(step.Settings, "searchPattern") ?? "*.*";
        var recursive = ModuleSettingReader.GetBoolean(step.Settings, "recursive", defaultValue: false, Descriptor.Id);

        if (!Directory.Exists(directory))
        {
            throw new ArgumentException($"Source directory '{directory}' was not found.", nameof(step));
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var filePaths = Directory.EnumerateFiles(directory, searchPattern, searchOption)
            .OrderBy(static filePath => filePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var payloads = new List<IntegrationPayload>(filePaths.Length);

        foreach (var filePath in filePaths)
        {
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["flowId"] = context.FlowId,
                ["executionId"] = context.ExecutionId,
                ["fullPath"] = filePath,
                ["directory"] = Path.GetDirectoryName(filePath) ?? directory,
                ["extension"] = Path.GetExtension(filePath),
                ["lastWriteTimeUtc"] = File.GetLastWriteTimeUtc(filePath).ToString("O")
            };

            payloads.Add(new IntegrationPayload(
                Path.GetFileName(filePath),
                BinaryData.FromBytes(bytes),
                ResolveContentType(filePath),
                metadata));
        }

        return new IntegrationBatch(payloads);
    }

    private static string ResolveContentType(string filePath)
    {
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
