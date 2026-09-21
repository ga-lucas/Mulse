using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class DocumentRepositoryFetchModule : IFetchModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("path", "Repository path", "The document repository root directory.", true),
        new("searchPattern", "Search pattern", "A wildcard pattern used to select repository documents.", false, ModuleSettingInputKind.Text, "*.*"),
        new("recursive", "Recursive", "Search subdirectories when enabled.", false, ModuleSettingInputKind.Boolean, "true"),
        new("includeMetadataSidecars", "Include metadata sidecars", "Loads sibling .metadata.json files into payload metadata when enabled.", false, ModuleSettingInputKind.Boolean, "true"),
        new("afterProcessing", "After processing", "What to do with a document once it has been fetched successfully, so it isn't re-processed on the next run.", false, ModuleSettingInputKind.Select, nameof(FetchPostProcessingAction.None), FetchPostProcessing.ActionOptions),
        new("archivePath", "Archive path", "Directory documents are moved into when 'After processing' is MoveToArchive. Defaults to a sibling '<path>.processed' folder.", false)
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

    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var repositoryPath = Path.GetFullPath(ModuleSettings.GetRequired(step.Settings, "path", Descriptor.Id));
        var searchPattern = ModuleSettings.GetOptional(step.Settings, "searchPattern") ?? "*.*";
        var recursive = ModuleSettings.GetBoolean(step.Settings, "recursive", defaultValue: true, Descriptor.Id);
        var includeMetadataSidecars = ModuleSettings.GetBoolean(step.Settings, "includeMetadataSidecars", defaultValue: true, Descriptor.Id);
        var afterProcessing = FetchPostProcessing.ParseAction(step.Settings, "afterProcessing", Descriptor.Id);
        var archivePath = ModuleSettings.GetOptional(step.Settings, "archivePath");

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
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                BinaryData.FromBytes(await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false)),
                ResolveContentType(filePath),
                metadata));

            FetchPostProcessing.Apply(filePath, repositoryPath, afterProcessing, archivePath, sidecarPath: filePath + ".metadata.json");
        }

        return new IntegrationBatch(payloads);
    }

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
        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".hl7" or ".adt" => "text/hl7-v2",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
