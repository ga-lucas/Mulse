namespace Mulse.Modules.BuiltIn.Fetch;

public sealed class FileSystemFetchModule : IFetchOutcomeModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Path", "The local or mounted directory to read files from.", true),
        new("searchPattern", "Search pattern", "A wildcard pattern used to select files from the directory.", false, ModuleSettingInputKind.Text, "*.*"),
        new("recursive", "Recursive", "Search subdirectories when enabled.", false, ModuleSettingInputKind.Boolean, "false"),
        new("afterProcessing", "After successful processing", "Applied only after every required processing and delivery stage succeeds, not when the file is read.", false, ModuleSettingInputKind.Select, nameof(FetchPostProcessingAction.None), FetchPostProcessing.ActionOptions),
        new("archivePath", "Archive path", "Directory files are moved into on success. Defaults to a sibling '<path>.processed' folder.", false),
        new("onFailure", "After permanent failure", "Optional failure action after processing/delivery retries are exhausted. None keeps the original for inspection; some destinations may already have received data.", false, ModuleSettingInputKind.Select, nameof(FetchPostProcessingAction.None), FetchPostProcessing.ActionOptions),
        new("failureArchivePath", "Failure archive path", "Failure move destination. Defaults to a sibling '<path>.failed' folder.", false)
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
        _ = FetchPostProcessing.ParseAction(step.Settings, "afterProcessing", Descriptor.Id);
        _ = FetchPostProcessing.ParseAction(step.Settings, "onFailure", Descriptor.Id);

        if (!Directory.Exists(directory))
        {
            throw new ArgumentException($"Source directory '{directory}' was not found.", nameof(step));
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var filePaths = Directory.EnumerateFiles(directory, searchPattern, searchOption)
            .OrderBy(static filePath => filePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var payloads = new List<IntegrationPayload>(filePaths.Length);
        var receipts = new List<FileFetchProcessing.Receipt>(filePaths.Length);
        var pending = FileFetchProcessing.PendingFiles(context.PendingSourceReceipts, step.Module);
        foreach (var filePath in filePaths)
        {
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (FileFetchProcessing.IsPending(filePath, bytes, pending)) continue;
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

            receipts.Add(await FileFetchProcessing.CaptureAsync(filePath, directory, bytes, context.ExecutionId, step, null, cancellationToken).ConfigureAwait(false));
        }

        return new IntegrationBatch(payloads) { SourceReceipt = receipts.Count == 0 ? null : FileFetchProcessing.Pack(receipts) };
    }

    public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken cancellationToken)
        => FileFetchProcessing.CompleteAsync(receipt, outcome, cancellationToken);

    private static string ResolveContentType(string filePath)
    {
        return ContentTypeResolver.FromFileName(filePath);
    }
}
