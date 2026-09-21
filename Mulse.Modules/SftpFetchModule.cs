using System.Text.RegularExpressions;
using Renci.SshNet;

namespace Mulse.Modules;

public sealed class SftpFetchModule : IFetchModule
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
        new("remotePath", "Remote path", "The remote directory to poll for files.", true),
        new("searchPattern", "Search pattern", "A wildcard pattern used to select remote files.", false, ModuleSettingInputKind.Text, "*.*"),
        new("maxFiles", "Max files per poll", "Limits how many files are read during one execution.", false, ModuleSettingInputKind.Number, "50"),
        new("deleteAfterRead", "Delete after read", "Deletes each remote file after it has been fetched successfully.", false, ModuleSettingInputKind.Boolean, "false")
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "sftp-fetch",
        "SFTP fetch",
        ModuleKind.Fetch,
        "Polls an SFTP location for raw files and emits them into the flow for downstream parsing.",
        SettingDescriptors,
        Recommendation);

    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
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
        var deleteAfterRead = ModuleSettingReader.GetBoolean(step.Settings, "deleteAfterRead", defaultValue: false, Descriptor.Id);

        using var client = new SftpClient(host, port, username, password);
        client.Connect();

        try
        {
            if (!client.Exists(remotePath))
            {
                throw new ArgumentException($"Remote SFTP path '{remotePath}' was not found.", nameof(step));
            }

            var files = client.ListDirectory(remotePath)
                .Where(static entry => !entry.IsDirectory && !entry.IsSymbolicLink)
                .Where(entry => IsMatch(entry.Name, searchPattern))
                .OrderBy(static entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                .Take(Math.Max(maxFiles, 1))
                .ToArray();

            var payloads = new List<IntegrationPayload>(files.Length);
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using var buffer = new MemoryStream();
                await using (var remoteStream = client.OpenRead(file.FullName))
                {
                    await remoteStream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
                }

                payloads.Add(new IntegrationPayload(
                    file.Name,
                    BinaryData.FromBytes(buffer.ToArray()),
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

                if (deleteAfterRead)
                {
                    client.DeleteFile(file.FullName);
                }
            }

            return new IntegrationBatch(payloads);
        }
        finally
        {
            client.Disconnect();
        }
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
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
