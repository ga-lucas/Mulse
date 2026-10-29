using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class DocumentRepositoryFetchModule : IFetchOutcomeModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Repository path", "The document repository root directory.", true),
        new("searchPattern", "Search pattern", "A wildcard pattern used to select repository documents.", false, ModuleSettingInputKind.Text, "*.*"),
        new("recursive", "Recursive", "Search subdirectories when enabled.", false, ModuleSettingInputKind.Boolean, "true"),
        new("includeMetadataSidecars", "Include metadata sidecars", "Loads sibling .metadata.json files into payload metadata when enabled.", false, ModuleSettingInputKind.Boolean, "true"),
        new("afterProcessing", "After successful processing", "Applied only after all processing and deliveries succeed, not immediately after reading.", false, ModuleSettingInputKind.Select, nameof(FetchPostProcessingAction.None), FetchPostProcessing.ActionOptions),
        new("archivePath", "Archive path", "Success archive directory. Defaults to a sibling '<path>.processed' folder.", false),
        new("onFailure", "After permanent failure", "Failure action after retries are exhausted. None keeps the original; delivery may have partially succeeded.", false, ModuleSettingInputKind.Select, nameof(FetchPostProcessingAction.None), FetchPostProcessing.ActionOptions),
        new("failureArchivePath", "Failure archive path", "Failure archive directory. Defaults to a sibling '<path>.failed' folder.", false)
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.FileSystem],
        [ModuleCapability.Ingestion, ModuleCapability.Storage]);

    public ModuleDescriptor Descriptor { get; } = new(
        "document-repository-fetch",
        "Document repository fetch",
        ModuleKind.Fetch,
        "Reads source documents from a repository-style directory and merges optional sidecar metadata into the batch.",
        SettingDescriptors,
        Recommendation);

    /// <summary><paramref name="input"/> is unused: this is a root source that reads from a repository directory only.</summary>
    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var repositoryPath = Path.GetFullPath(ModuleSettings.GetRequired(step.Settings, "path", Descriptor.Id));
        var searchPattern = ModuleSettings.GetOptional(step.Settings, "searchPattern") ?? "*.*";
        var recursive = ModuleSettings.GetBoolean(step.Settings, "recursive", defaultValue: true, Descriptor.Id);
        var includeMetadataSidecars = ModuleSettings.GetBoolean(step.Settings, "includeMetadataSidecars", defaultValue: true, Descriptor.Id);
        _ = FetchPostProcessing.ParseAction(step.Settings, "afterProcessing", Descriptor.Id);
        _ = FetchPostProcessing.ParseAction(step.Settings, "onFailure", Descriptor.Id);

        if (!Directory.Exists(repositoryPath))
        {
            throw new DirectoryNotFoundException($"Document repository '{repositoryPath}' was not found.");
        }

        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var filePaths = Directory.EnumerateFiles(repositoryPath, searchPattern, searchOption)
            .Where(static path => !path.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var payloads = new List<IntegrationPayload>(filePaths.Length);
        var receipts = new List<FileFetchProcessing.Receipt>(filePaths.Length);
        var pending = FileFetchProcessing.PendingFiles(context.PendingSourceReceipts, step.Module);
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (FileFetchProcessing.IsPending(filePath, bytes, pending)) continue;
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["flowId"] = context.FlowId,
                ["executionId"] = context.ExecutionId,
                ["repositoryPath"] = repositoryPath,
                ["repositoryRelativePath"] = Path.GetRelativePath(repositoryPath, filePath),
                ["fullPath"] = filePath,
                ["extension"] = Path.GetExtension(filePath),
                ["lastWriteTimeUtc"] = File.GetLastWriteTimeUtc(filePath).ToString("O")
            };

            if (includeMetadataSidecars)
            {
                foreach (var entry in await ReadSidecarMetadataAsync(filePath, cancellationToken).ConfigureAwait(false))
                {
                    metadata[entry.Key] = entry.Value;
                }
            }

            payloads.Add(new IntegrationPayload(
                Path.GetFileName(filePath),
                BinaryData.FromBytes(bytes),
                ResolveContentType(filePath),
                metadata));

            receipts.Add(await FileFetchProcessing.CaptureAsync(filePath, repositoryPath, bytes, context.ExecutionId, step, filePath + ".metadata.json", cancellationToken).ConfigureAwait(false));
        }

        return new IntegrationBatch(payloads) { SourceReceipt = receipts.Count == 0 ? null : FileFetchProcessing.Pack(receipts) };
    }

    public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken cancellationToken)
        => FileFetchProcessing.CompleteAsync(receipt, outcome, cancellationToken);

    private static async Task<IReadOnlyDictionary<string, string>> ReadSidecarMetadataAsync(string filePath, CancellationToken cancellationToken)
    {
        var sidecarPath = filePath + ".metadata.json";
        if (!File.Exists(sidecarPath))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var json = await File.ReadAllTextAsync(sidecarPath, cancellationToken).ConfigureAwait(false);
        return CompatibilityPayloadNavigator.ParseScalarObject(json, "document-repository-fetch", "includeMetadataSidecars");
    }

    private static string ResolveContentType(string filePath)
    {
        return ContentTypeResolver.FromFileName(filePath);
    }
}
