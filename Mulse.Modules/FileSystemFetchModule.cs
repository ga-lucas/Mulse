namespace Mulse.Modules;

public sealed class FileSystemFetchModule : IFetchModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Path", "The local or mounted directory to read files from.", true),
        new("searchPattern", "Search pattern", "A wildcard pattern used to select files from the directory.", false, ModuleSettingInputKind.Text, "*.*"),
        new("recursive", "Recursive", "Search subdirectories when enabled.", false, ModuleSettingInputKind.Boolean, "false"),
        new("afterProcessing", "After processing", "What to do with a file once it has been fetched successfully, so it isn't re-processed on the next run.", false, ModuleSettingInputKind.Select, nameof(FetchPostProcessingAction.None), FetchPostProcessing.ActionOptions),
        new("archivePath", "Archive path", "Directory files are moved into when 'After processing' is MoveToArchive. Defaults to a sibling '<path>.processed' folder.", false)
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

    /// <summary><paramref name="input"/> is unused: this is a root source that reads from the file system only.</summary>
    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(ModuleSettingReader.GetRequired(step.Settings, "path", Descriptor.Id));
        var searchPattern = ModuleSettingReader.GetOptional(step.Settings, "searchPattern") ?? "*.*";
        var recursive = ModuleSettingReader.GetBoolean(step.Settings, "recursive", defaultValue: false, Descriptor.Id);
        var afterProcessing = FetchPostProcessing.ParseAction(step.Settings, "afterProcessing", Descriptor.Id);
        var archivePath = ModuleSettingReader.GetOptional(step.Settings, "archivePath");

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

            FetchPostProcessing.Apply(filePath, directory, afterProcessing, archivePath);
        }

        return new IntegrationBatch(payloads);
    }

    private static string ResolveContentType(string filePath)
    {
        return ContentTypeResolver.FromFileName(filePath);
    }
}
