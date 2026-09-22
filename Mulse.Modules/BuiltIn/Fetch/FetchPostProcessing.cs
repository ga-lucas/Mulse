namespace Mulse.Modules.BuiltIn.Fetch;

/// <summary>
/// Shared post-fetch file handling so any file-oriented fetch module (local directory, document
/// repository, and future contributed modules) can offer the same archive/delete/rename semantics
/// BizTalk receive locations always applied after picking up a file, without every module
/// reimplementing file-move/collision/relative-path logic itself.
/// </summary>
public static class FetchPostProcessing
{
    /// <summary>Shared <see cref="ModuleSettingDescriptor"/> options for an "after processing" select setting.</summary>
    public static readonly IReadOnlyList<ModuleSettingOption> ActionOptions =
    [
        new(nameof(FetchPostProcessingAction.None), "Leave in place (may re-process on next run)"),
        new(nameof(FetchPostProcessingAction.Delete), "Delete after successful fetch"),
        new(nameof(FetchPostProcessingAction.MoveToArchive), "Move to an archive folder after successful fetch"),
        new(nameof(FetchPostProcessingAction.Rename), "Rename in place after successful fetch")
    ];

    /// <summary>Parses an "after processing" setting value, defaulting to <see cref="FetchPostProcessingAction.None"/> when absent.</summary>
    public static FetchPostProcessingAction ParseAction(IReadOnlyDictionary<string, string> settings, string settingName, string moduleId)
    {
        var raw = ModuleSettingReader.GetOptional(settings, settingName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return FetchPostProcessingAction.None;
        }

        if (Enum.TryParse<FetchPostProcessingAction>(raw, ignoreCase: true, out var action))
        {
            return action;
        }

        throw new ArgumentException(
            $"Module '{moduleId}' has an invalid '{settingName}' value '{raw}'. Expected one of: {string.Join(", ", Enum.GetNames<FetchPostProcessingAction>())}.",
            settingName);
    }

    /// <summary>
    /// Applies the resolved post-processing action to a successfully-fetched file (and, if present,
    /// its sidecar metadata file) so it isn't picked up again on the next run.
    /// </summary>
    /// <param name="filePath">The full path of the file that was just fetched.</param>
    /// <param name="rootPath">The repository/search root the file was found under (used to preserve relative structure when archiving).</param>
    /// <param name="action">The action to apply.</param>
    /// <param name="archivePath">An explicit archive directory, or null to use a sibling "&lt;root&gt;.processed" folder next to <paramref name="rootPath"/>.</param>
    /// <param name="sidecarPath">An optional sidecar file (e.g. metadata) that should move/delete alongside <paramref name="filePath"/>.</param>
    public static void Apply(string filePath, string rootPath, FetchPostProcessingAction action, string? archivePath, string? sidecarPath = null)
    {
        switch (action)
        {
            case FetchPostProcessingAction.None:
                return;

            case FetchPostProcessingAction.Delete:
                File.Delete(filePath);
                if (sidecarPath is not null && File.Exists(sidecarPath))
                {
                    File.Delete(sidecarPath);
                }

                return;

            case FetchPostProcessingAction.MoveToArchive:
                var destination = MoveToArchive(filePath, rootPath, archivePath);
                if (sidecarPath is not null && File.Exists(sidecarPath))
                {
                    MoveToArchive(sidecarPath, rootPath, archivePath, forcedDestination: destination + Path.GetExtension(sidecarPath));
                }

                return;

            case FetchPostProcessingAction.Rename:
                RenameInPlace(filePath);
                if (sidecarPath is not null && File.Exists(sidecarPath))
                {
                    RenameInPlace(sidecarPath);
                }

                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown fetch post-processing action.");
        }
    }

    private static string MoveToArchive(string filePath, string rootPath, string? archivePath, string? forcedDestination = null)
    {
        var targetDirectory = ResolveArchiveDirectory(rootPath, archivePath);
        var relativePath = Path.GetRelativePath(rootPath, filePath);
        var destinationPath = forcedDestination ?? Path.GetFullPath(Path.Combine(targetDirectory, relativePath));

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? targetDirectory);
        destinationPath = ResolveCollisionFreePath(destinationPath);
        File.Move(filePath, destinationPath);
        return destinationPath;
    }

    /// <summary>
    /// Resolves the archive directory. Defaults to a *sibling* of <paramref name="rootPath"/>
    /// (e.g. "C:\data\in" -&gt; "C:\data\in.processed") rather than a subfolder, so a recursive
    /// search of <paramref name="rootPath"/> never re-discovers already-archived files.
    /// </summary>
    private static string ResolveArchiveDirectory(string rootPath, string? archivePath)
    {
        if (!string.IsNullOrWhiteSpace(archivePath))
        {
            return Path.GetFullPath(archivePath);
        }

        var trimmedRoot = rootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmedRoot + ".processed";
    }

    private static string RenameInPlace(string filePath)
    {
        var destinationPath = ResolveCollisionFreePath(filePath + ".processed");
        File.Move(filePath, destinationPath);
        return destinationPath;
    }

    private static string ResolveCollisionFreePath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath) ?? string.Empty;
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);

        for (var attempt = 1; ; attempt++)
        {
            var candidate = Path.Combine(directory, $"{fileNameWithoutExtension}-{attempt}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
