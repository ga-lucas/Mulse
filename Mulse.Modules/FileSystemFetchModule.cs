namespace Mulse.Modules;

public sealed class FileSystemFetchModule : IFetchModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Path", "The local or mounted directory to read files from.", true),
        new("searchPattern", "Search pattern", "A wildcard pattern used to select files from the directory.", false, ModuleSettingInputKind.Text, "*.*"),
        new("recursive", "Recursive", "Search subdirectories when enabled.", false, ModuleSettingInputKind.Boolean, "false")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.FileSystem],
        [ModuleCapability.Ingestion, ModuleCapability.Polling]);

    public ModuleDescriptor Descriptor { get; } = new(
        "file-system-fetch",
        "File system fetch",
        ModuleKind.Fetch,
        "Reads raw files from a local or mounted directory so downstream parse modules can normalize them.",
        SettingDescriptors,
        Recommendation);

    public async Task<IntegrationBatch> FetchAsync(
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
            payloads.Add(new IntegrationPayload(
                Path.GetFileName(filePath),
                BinaryData.FromBytes(bytes),
                ResolveContentType(filePath),
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["flowId"] = context.FlowId,
                    ["executionId"] = context.ExecutionId,
                    ["fullPath"] = filePath,
                    ["directory"] = Path.GetDirectoryName(filePath) ?? directory,
                    ["extension"] = Path.GetExtension(filePath),
                    ["lastWriteTimeUtc"] = File.GetLastWriteTimeUtc(filePath).ToString("O")
                }));
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
