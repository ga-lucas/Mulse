using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace Mulse.Modules.BuiltIn.Fetch;

public sealed class SftpFetchModule : IFetchOutcomeModule
{
    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.Sftp],
        [ModuleCapability.Ingestion, ModuleCapability.Polling]);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("host", "Host", "The SFTP host name or IP address.", true),
        new("port", "Port", "The SFTP port.", true, ModuleSettingInputKind.Number, "22"),
        new("username", "Username", "The SFTP account user name.", true),
        new("password", "Password", "The SFTP account password.", true, ModuleSettingInputKind.Password),
        new("remotePath", "Remote path", "The remote directory scanned once when this fetch executes. Configure repeated scans with the flow's Interval trigger.", true),
        new("searchPattern", "Search pattern", "A wildcard pattern used to select remote files.", false, ModuleSettingInputKind.Text, "*.*"),
        new("maxFiles", "Max files per execution", "Limits how many files are read during one flow execution; this is not a polling interval.", false, ModuleSettingInputKind.Number, "50"),
        new("deleteAfterRead", "Delete after successful processing", "Deletes remote files only after all processing and deliveries succeed. Kept for compatibility with the original setting name.", false, ModuleSettingInputKind.Boolean, "false"),
        new("failureRemotePath", "Failure directory", "Optional existing remote directory to quarantine files after permanent processing/delivery failure. Blank keeps originals. Some destinations may already have received data.", false)
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "sftp-fetch",
        "SFTP fetch",
        ModuleKind.Fetch,
        "Scans an SFTP directory once per flow execution and reads matching files. Use the flow's Interval trigger for repeated polling or OnDemand for manual runs; this module does not initiate Push runs.",
        SettingDescriptors,
        Recommendation);

    /// <summary><paramref name="input"/> is unused: this root source scans an SFTP location once per execution.</summary>
    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var host = ModuleSettingReader.GetRequired(step.Settings, "host", Descriptor.Id);
        var port = ModuleSettingReader.GetInt32(step.Settings, "port", 22, Descriptor.Id);
        var username = ModuleSettingReader.GetRequired(step.Settings, "username", Descriptor.Id);
        var password = ModuleSettingReader.GetRequired(step.Settings, "password", Descriptor.Id);
        var remotePath = ModuleSettingReader.GetRequired(step.Settings, "remotePath", Descriptor.Id);
        var searchPattern = ModuleSettingReader.GetOptional(step.Settings, "searchPattern") ?? "*.*";
        var maxFiles = ModuleSettingReader.GetInt32(step.Settings, "maxFiles", 50, Descriptor.Id);
        _ = ModuleSettingReader.GetBoolean(step.Settings, "deleteAfterRead", defaultValue: false, Descriptor.Id);
        var failureDirectory = ModuleSettingReader.GetOptional(step.Settings, "failureRemotePath");

        using var client = new SftpClient(host, port, username, password);
        client.Connect();

        try
        {
            if (!client.Exists(remotePath))
            {
                throw new ArgumentException($"Remote SFTP path '{remotePath}' was not found.", nameof(step));
            }
            if (!string.IsNullOrWhiteSpace(failureDirectory) && (!client.Exists(failureDirectory) || !client.GetAttributes(failureDirectory).IsDirectory))
                throw new ArgumentException($"SFTP failure directory '{failureDirectory}' was not found.", nameof(step));

            var files = client.ListDirectory(remotePath)
                .Where(static entry => !entry.IsDirectory && !entry.IsSymbolicLink)
                .Where(entry => IsMatch(entry.Name, searchPattern))
                .OrderBy(static entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var payloads = new List<IntegrationPayload>();
            var receipts = new List<RemoteFileReceipt>();
            var pending = context.PendingSourceReceipts.Where(receipt => receipt.Step.Module.Equals(step.Module, StringComparison.OrdinalIgnoreCase)
                    && ModuleSettingReader.GetRequired(receipt.Step.Settings, "host", Descriptor.Id).Equals(host, StringComparison.OrdinalIgnoreCase)
                    && ModuleSettingReader.GetInt32(receipt.Step.Settings, "port", 22, Descriptor.Id) == port
                    && ModuleSettingReader.GetRequired(receipt.Step.Settings, "username", Descriptor.Id) == username)
                .SelectMany(Unpack).ToArray();
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var buffer = new MemoryStream();
                await using (var remoteStream = client.OpenRead(file.FullName))
                {
                    await remoteStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                }

                var bytes = buffer.ToArray();
                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (pending.Any(source => source.Path == file.FullName && source.Length == bytes.LongLength
                    && source.LastWriteTicks == file.LastWriteTimeUtc.Ticks && source.Hash == hash)) continue;
                payloads.Add(new IntegrationPayload(
                    file.Name,
                    BinaryData.FromBytes(bytes),
                    ResolveContentType(file.Name),
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["flowId"] = context.FlowId,
                        ["executionId"] = context.ExecutionId,
                        ["remotePath"] = file.FullName,
                        ["directory"] = remotePath,
                        ["extension"] = Path.GetExtension(file.Name),
                        ["lastWriteTimeUtc"] = file.LastWriteTimeUtc.ToString("O")
                    }));

                receipts.Add(new RemoteFileReceipt(file.FullName, bytes.LongLength, file.LastWriteTimeUtc.Ticks,
                    hash,
                    string.IsNullOrWhiteSpace(failureDirectory) ? null : failureDirectory.TrimEnd('/') + "/" + file.Name + "." + context.ExecutionId));
                if (receipts.Count >= Math.Max(maxFiles, 1)) break;
            }

            return new IntegrationBatch(payloads)
            {
                SourceReceipt = receipts.Count == 0 ? null : new Dictionary<string, string> { ["files"] = JsonSerializer.Serialize(receipts) }
            };
        }
        finally
        {
            client.Disconnect();
        }
    }

    private sealed record RemoteFileReceipt(string Path, long Length, long LastWriteTicks, string Hash, string? FailureTarget);

    private static IReadOnlyList<RemoteFileReceipt> Unpack(FetchProcessingReceipt receipt)
    {
        if (!receipt.Data.TryGetValue("files", out var json)) throw new InvalidOperationException("SFTP source receipt has no file identities.");
        return JsonSerializer.Deserialize<List<RemoteFileReceipt>>(json)
            ?? throw new InvalidOperationException("SFTP source receipt is empty.");
    }

    public async Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken cancellationToken)
    {
        var settings = receipt.Step.Settings;
        var delete = outcome.Succeeded && ModuleSettingReader.GetBoolean(settings, "deleteAfterRead", false, Descriptor.Id);
        if (!delete && (outcome.Succeeded || string.IsNullOrWhiteSpace(ModuleSettingReader.GetOptional(settings, "failureRemotePath")))) return;
        var files = Unpack(receipt);
        using var client = new SftpClient(ModuleSettingReader.GetRequired(settings, "host", Descriptor.Id),
            ModuleSettingReader.GetInt32(settings, "port", 22, Descriptor.Id),
            ModuleSettingReader.GetRequired(settings, "username", Descriptor.Id),
            ModuleSettingReader.GetRequired(settings, "password", Descriptor.Id));
        client.Connect();
        try
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!client.Exists(file.Path))
                {
                    if (delete) continue;
                    if (file.FailureTarget is { } completed && client.Exists(completed) && await MatchesAsync(client, completed, file, cancellationToken).ConfigureAwait(false)) continue;
                    throw new IOException($"SFTP source '{file.Path}' and its expected quarantine file are missing.");
                }
                if (client.GetAttributes(file.Path).LastWriteTimeUtc.Ticks != file.LastWriteTicks || !await MatchesAsync(client, file.Path, file, cancellationToken).ConfigureAwait(false))
                    throw new IOException($"SFTP source '{file.Path}' changed after fetch; refusing to remove or move the replacement.");
                if (delete) client.DeleteFile(file.Path);
                else
                {
                    if (file.FailureTarget is not { } target) throw new InvalidOperationException("SFTP receipt has no quarantine destination.");
                    if (client.Exists(target)) throw new IOException($"SFTP quarantine destination '{target}' already exists.");
                    client.RenameFile(file.Path, target);
                }
            }
        }
        finally { client.Disconnect(); }
    }

    private static async Task<bool> MatchesAsync(SftpClient client, string path, RemoteFileReceipt receipt, CancellationToken cancellationToken)
    {
        if (client.GetAttributes(path).Size != receipt.Length) return false;
        await using var stream = client.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)) == receipt.Hash;
    }

    private static bool IsMatch(string fileName, string searchPattern)
    {
        var regexPattern = "^" + Regex.Escape(searchPattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string ResolveContentType(string fileName)
    {
        return ContentTypeResolver.FromFileName(fileName);
    }
}
