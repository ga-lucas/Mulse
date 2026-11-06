using FluentFTP;

namespace Mulse.Modules.BuiltIn.Deliver;

public sealed class FtpDeliverModule : IDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("host", "Host", "The FTP/FTPS host name or IP address.", true),
        new("port", "Port", "The control-connection port (21 for FTP/explicit FTPS, 990 for implicit FTPS).", false, ModuleSettingInputKind.Number, "21"),
        new("username", "Username", "The FTP account user name.", true),
        new("password", "Password", "The FTP account password.", true, ModuleSettingInputKind.Password),
        new("encryptionMode", "Encryption", "None = plain FTP. Explicit = AUTH TLS upgrade. Implicit = TLS from connect (typically port 990). Auto = implicit on port 990, otherwise try explicit TLS and fall back to plain FTP.", false, ModuleSettingInputKind.Select, "Auto",
            [new("Auto", "Auto (by port)"), new("None", "None (plain FTP)"), new("Explicit", "Explicit FTPS"), new("Implicit", "Implicit FTPS")]),
        new("acceptAnyCertificate", "Accept any server certificate", "Skips TLS certificate validation. BizTalk's nsoftware adapter commonly ran this way; enabling it removes protection against server impersonation.", false, ModuleSettingInputKind.Boolean, "false"),
        new("passive", "Passive mode", "Uses passive data connections (recommended behind firewalls). Off uses active mode.", false, ModuleSettingInputKind.Boolean, "true"),
        new("remotePath", "Remote path", "The remote directory the file is written to.", true),
        new("remoteFileName", "Remote file name", "File name template. Supports {{payloadName}}, {{flowId}}, {{executionId}} and {{metadata.key}}. Blank uses the payload name.", false, ModuleSettingInputKind.Text, "{{payloadName}}"),
        new("overwrite", "Overwrite existing", "Replaces an existing remote file. When off, a collision fails the delivery explicitly.", false, ModuleSettingInputKind.Boolean, "false"),
        new("append", "Append to existing", "Appends to an existing remote file instead of replacing it. Retried deliveries can duplicate appended data.", false, ModuleSettingInputKind.Boolean, "false"),
        new("remoteTempPath", "Remote temp directory", "Optional directory. The file is uploaded here first and then moved into place so consumers never see a partial file.", false),
        new("timeoutSeconds", "Timeout (seconds)", "Connection/operation timeout.", false, ModuleSettingInputKind.Number, "60")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.Ftp],
        [ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "ftp-deliver",
        "FTP/FTPS deliver",
        ModuleKind.Deliver,
        "Uploads rendered payloads to an FTP or FTPS (explicit/implicit) directory.",
        SettingDescriptors,
        Recommendation);

    public async Task DeliverAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var settings = step.Settings;
        var host = ModuleSettingReader.GetRequired(settings, "host", Descriptor.Id);
        var port = ModuleSettingReader.GetInt32(settings, "port", 21, Descriptor.Id);
        var username = ModuleSettingReader.GetRequired(settings, "username", Descriptor.Id);
        var password = ModuleSettingReader.GetRequired(settings, "password", Descriptor.Id);
        var mode = ModuleSettingReader.GetOptional(settings, "encryptionMode") ?? "Auto";
        var remotePath = ModuleSettingReader.GetRequired(settings, "remotePath", Descriptor.Id);
        var nameTemplate = ModuleSettingReader.GetOptional(settings, "remoteFileName");
        var overwrite = ModuleSettingReader.GetBoolean(settings, "overwrite", false, Descriptor.Id);
        var append = ModuleSettingReader.GetBoolean(settings, "append", false, Descriptor.Id);
        var tempPath = ModuleSettingReader.GetOptional(settings, "remoteTempPath");
        var timeout = Math.Max(1, ModuleSettingReader.GetInt32(settings, "timeoutSeconds", 60, Descriptor.Id)) * 1000;
        if (append && !string.IsNullOrWhiteSpace(tempPath))
            throw new ArgumentException($"Module '{Descriptor.Id}' cannot combine append with remoteTempPath.");

        var encryption = mode.ToLowerInvariant() switch
        {
            "auto" => port == 990 ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Auto,
            "none" => FtpEncryptionMode.None,
            "explicit" => FtpEncryptionMode.Explicit,
            "implicit" => FtpEncryptionMode.Implicit,
            _ => throw new ArgumentException($"Module '{Descriptor.Id}' has unsupported encryptionMode '{mode}'.")
        };

        await using var client = new AsyncFtpClient(host, username, password, port);
        client.Config.EncryptionMode = encryption;
        client.Config.ValidateAnyCertificate = ModuleSettingReader.GetBoolean(settings, "acceptAnyCertificate", false, Descriptor.Id);
        client.Config.DataConnectionType = ModuleSettingReader.GetBoolean(settings, "passive", true, Descriptor.Id)
            ? FtpDataConnectionType.AutoPassive : FtpDataConnectionType.AutoActive;
        client.Config.ConnectTimeout = timeout;
        client.Config.ReadTimeout = timeout;
        client.Config.DataConnectionConnectTimeout = timeout;
        client.Config.DataConnectionReadTimeout = timeout;
        await client.Connect(cancellationToken).ConfigureAwait(false);

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = RemoteDeliverNaming.ResolveFileName(nameTemplate, context, payload);
            var target = RemoteDeliverNaming.Combine(remotePath, name);
            var exists = await client.FileExists(target, cancellationToken).ConfigureAwait(false);
            if (exists && !overwrite && !append)
                throw new IOException($"Remote FTP file '{target}' already exists and overwrite is disabled.");

            var bytes = payload.Content.ToArray();
            if (append)
            {
                await Ensure(client.UploadBytes(bytes, target, FtpRemoteExists.AddToEnd, false, null, cancellationToken), target).ConfigureAwait(false);
            }
            else if (string.IsNullOrWhiteSpace(tempPath))
            {
                await Ensure(client.UploadBytes(bytes, target, FtpRemoteExists.Overwrite, false, null, cancellationToken), target).ConfigureAwait(false);
            }
            else
            {
                var temp = RemoteDeliverNaming.Combine(tempPath, name + "." + context.ExecutionId + ".tmp");
                await Ensure(client.UploadBytes(bytes, temp, FtpRemoteExists.Overwrite, false, null, cancellationToken), temp).ConfigureAwait(false);
                if (!await client.MoveFile(temp, target, FtpRemoteExists.Overwrite, cancellationToken).ConfigureAwait(false))
                    throw new IOException($"FTP server could not move '{temp}' to '{target}'.");
            }
        }

        await client.Disconnect(cancellationToken).ConfigureAwait(false);
    }

    private static async Task Ensure(Task<FtpStatus> upload, string path)
    {
        if (await upload.ConfigureAwait(false) is not (FtpStatus.Success))
            throw new IOException($"FTP upload to '{path}' did not complete successfully.");
    }
}
