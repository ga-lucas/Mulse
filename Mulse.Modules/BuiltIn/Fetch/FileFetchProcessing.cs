using System.Security.Cryptography;
using System.Text.Json;

namespace Mulse.Modules.BuiltIn.Fetch;

/// <summary>Source-file receipts keep cleanup tied to the bytes actually fetched, not a later directory scan.</summary>
public static class FileFetchProcessing
{
    public sealed record FileVersion(string Path, long Length, long LastWriteTicks, string Hash);
    public sealed record Receipt(FileVersion Source, FileVersion? Sidecar, FetchPostProcessingAction SuccessAction,
        string? SuccessTarget, FetchPostProcessingAction FailureAction, string? FailureTarget);

    public static async Task<Receipt> CaptureAsync(string path, string root, byte[] bytes, string executionId,
        ModuleStepDefinition step, string? sidecarPath, CancellationToken cancellationToken)
    {
        var success = FetchPostProcessing.ParseAction(step.Settings, "afterProcessing", step.Module);
        var failure = FetchPostProcessing.ParseAction(step.Settings, "onFailure", step.Module);
        var successTarget = Target(path, root, success, ModuleSettingReader.GetOptional(step.Settings, "archivePath"), ".processed", executionId);
        var failureTarget = Target(path, root, failure, ModuleSettingReader.GetOptional(step.Settings, "failureArchivePath"), ".failed", executionId);
        FileVersion? sidecar = null;
        if (sidecarPath is not null && File.Exists(sidecarPath))
            sidecar = Version(sidecarPath, await File.ReadAllBytesAsync(sidecarPath, cancellationToken).ConfigureAwait(false));
        return new Receipt(Version(path, bytes), sidecar, success, successTarget, failure, failureTarget);
    }

    public static IReadOnlyDictionary<string, string> Pack(IReadOnlyList<Receipt> receipts)
        => new Dictionary<string, string> { ["files"] = JsonSerializer.Serialize(receipts) };

    public static IReadOnlyList<FileVersion> PendingFiles(IReadOnlyList<FetchProcessingReceipt> receipts, string moduleId)
        => receipts.Where(receipt => receipt.Step.Module.Equals(moduleId, StringComparison.OrdinalIgnoreCase))
            .SelectMany(receipt => Unpack(receipt).Select(file => file.Source)).ToArray();

    public static bool IsPending(string path, byte[] bytes, IReadOnlyList<FileVersion> pending)
    {
        var matching = pending.Where(file => file.Path.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)
            && file.Length == bytes.LongLength && file.LastWriteTicks == File.GetLastWriteTimeUtc(path).Ticks).ToArray();
        if (matching.Length == 0) return false;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return matching.Any(file => file.Hash == hash);
    }

    private static IReadOnlyList<Receipt> Unpack(FetchProcessingReceipt receipt)
    {
        if (!receipt.Data.TryGetValue("files", out var json))
            throw new InvalidOperationException("The file source receipt does not contain file identities.");
        return JsonSerializer.Deserialize<List<Receipt>>(json)
            ?? throw new InvalidOperationException("The file source receipt is empty.");
    }

    public static async Task CompleteAsync(FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken cancellationToken)
    {
        foreach (var file in Unpack(receipt))
        {
            var action = outcome.Succeeded ? file.SuccessAction : file.FailureAction;
            var target = outcome.Succeeded ? file.SuccessTarget : file.FailureTarget;
            await ApplyAsync(file.Source, action, target, cancellationToken).ConfigureAwait(false);
            if (file.Sidecar is { } sidecar)
                await ApplyAsync(sidecar, action, target is null ? null : target + ".metadata.json", cancellationToken).ConfigureAwait(false);
        }
    }

    private static FileVersion Version(string path, byte[] bytes)
        => new(Path.GetFullPath(path), bytes.LongLength, File.GetLastWriteTimeUtc(path).Ticks, Convert.ToHexString(SHA256.HashData(bytes)));

    private static string? Target(string path, string root, FetchPostProcessingAction action, string? archive, string suffix, string executionId)
    {
        if (action is FetchPostProcessingAction.None or FetchPostProcessingAction.Delete) return null;
        var target = action == FetchPostProcessingAction.Rename
            ? path + suffix
            : Path.Combine(string.IsNullOrWhiteSpace(archive) ? root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + suffix : Path.GetFullPath(archive), Path.GetRelativePath(root, path));
        target = Path.GetFullPath(target);
        if (string.Equals(target, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The source cleanup destination must differ from the original file.");
        if (File.Exists(target) || File.Exists(target + ".metadata.json"))
            target = Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target) + "-" + executionId + Path.GetExtension(target));
        return target;
    }

    private static async Task ApplyAsync(FileVersion source, FetchPostProcessingAction action, string? target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action == FetchPostProcessingAction.None) return;
        if (!File.Exists(source.Path))
        {
            if (action == FetchPostProcessingAction.Delete) return;
            if (target is not null && File.Exists(target) && await HasExpectedContentAsync(target, source, cancellationToken).ConfigureAwait(false)) return;
            throw new IOException($"Source '{source.Path}' is missing and its expected cleanup destination was not found.");
        }
        if (File.GetLastWriteTimeUtc(source.Path).Ticks != source.LastWriteTicks || !await HasExpectedContentAsync(source.Path, source, cancellationToken).ConfigureAwait(false))
            throw new IOException($"Source '{source.Path}' changed after it was fetched; refusing to remove or move the replacement.");
        if (action == FetchPostProcessingAction.Delete) File.Delete(source.Path);
        else
        {
            if (target is null) throw new InvalidOperationException("A move/rename source receipt has no destination.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source.Path, target, overwrite: false);
        }
    }

    private static async Task<bool> HasExpectedContentAsync(string path, FileVersion expected, CancellationToken cancellationToken)
    {
        if (new FileInfo(path).Length != expected.Length) return false;
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) == expected.Hash;
    }
}
