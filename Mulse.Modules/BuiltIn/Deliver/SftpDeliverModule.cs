using Renci.SshNet;

namespace Mulse.Modules.BuiltIn.Deliver;

public sealed class SftpDeliverModule : IDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("host", "Host", "The SFTP host name or IP address.", true),
        new("port", "Port", "The SFTP port.", false, ModuleSettingInputKind.Number, "22"),
        new("username", "Username", "The SFTP account user name.", true),
        new("password", "Password", "The SFTP account password. Optional when a private key is configured.", false, ModuleSettingInputKind.Password),
        new("privateKey", "Private key (PEM/OpenSSH)", "Optional private key text for key authentication. Used instead of or in addition to the password.", false, ModuleSettingInputKind.Password),
        new("privateKeyPassphrase", "Private key passphrase", "Optional passphrase protecting the private key.", false, ModuleSettingInputKind.Password),
        new("hostKeyFingerprint", "Host key SHA-256 fingerprint", "The server's base64 SHA-256 host-key fingerprint (as shown by ssh-keygen -lf, without the 'SHA256:' prefix). Required unless Accept any host key is enabled.", false),
        new("acceptAnyHostKey", "Accept any host key", "Skips host identity verification. BizTalk's nsoftware adapter commonly ran this way; enabling it removes protection against server impersonation.", false, ModuleSettingInputKind.Boolean, "false"),
        new("remotePath", "Remote path", "The existing remote directory the file is written to.", true),
        new("remoteFileName", "Remote file name", "File name template. Supports {{payloadName}}, {{flowId}}, {{executionId}} and {{metadata.key}}. Blank uses the payload name.", false, ModuleSettingInputKind.Text, "{{payloadName}}"),
        new("overwrite", "Overwrite existing", "Replaces an existing remote file. When off, a collision fails the delivery explicitly.", false, ModuleSettingInputKind.Boolean, "false"),
        new("append", "Append to existing", "Appends to an existing remote file instead of replacing it. Retried deliveries can duplicate appended data.", false, ModuleSettingInputKind.Boolean, "false"),
        new("remoteTempPath", "Remote temp directory", "Optional existing directory. The file is uploaded here first and then renamed into place so consumers never see a partial file.", false),
        new("timeoutSeconds", "Timeout (seconds)", "Connection/operation timeout.", false, ModuleSettingInputKind.Number, "60")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.Sftp],
        [ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "sftp-deliver",
        "SFTP deliver",
        ModuleKind.Deliver,
        "Uploads rendered payloads to an SFTP directory using password and/or private-key authentication with host-key verification.",
        SettingDescriptors,
        Recommendation);

    public async Task DeliverAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var settings = step.Settings;
        var host = ModuleSettingReader.GetRequired(settings, "host", Descriptor.Id);
        var port = ModuleSettingReader.GetInt32(settings, "port", 22, Descriptor.Id);
        var username = ModuleSettingReader.GetRequired(settings, "username", Descriptor.Id);
        var password = ModuleSettingReader.GetOptional(settings, "password");
        var privateKey = ModuleSettingReader.GetOptional(settings, "privateKey");
        var passphrase = ModuleSettingReader.GetOptional(settings, "privateKeyPassphrase");
        var fingerprint = ModuleSettingReader.GetOptional(settings, "hostKeyFingerprint");
        var acceptAny = ModuleSettingReader.GetBoolean(settings, "acceptAnyHostKey", false, Descriptor.Id);
        var remotePath = ModuleSettingReader.GetRequired(settings, "remotePath", Descriptor.Id);
        var nameTemplate = ModuleSettingReader.GetOptional(settings, "remoteFileName");
        var overwrite = ModuleSettingReader.GetBoolean(settings, "overwrite", false, Descriptor.Id);
        var append = ModuleSettingReader.GetBoolean(settings, "append", false, Descriptor.Id);
        var tempPath = ModuleSettingReader.GetOptional(settings, "remoteTempPath");
        var timeout = TimeSpan.FromSeconds(Math.Max(1, ModuleSettingReader.GetInt32(settings, "timeoutSeconds", 60, Descriptor.Id)));

        if (string.IsNullOrWhiteSpace(password) && string.IsNullOrWhiteSpace(privateKey))
            throw new ArgumentException($"Module '{Descriptor.Id}' requires a password or a private key.");
        if (!acceptAny && string.IsNullOrWhiteSpace(fingerprint))
            throw new ArgumentException($"Module '{Descriptor.Id}' requires hostKeyFingerprint, or explicit acceptAnyHostKey=true.");
        if (append && !string.IsNullOrWhiteSpace(tempPath))
            throw new ArgumentException($"Module '{Descriptor.Id}' cannot combine append with remoteTempPath.");

        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(privateKey))
        {
            using var keyStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(privateKey));
            methods.Add(new PrivateKeyAuthenticationMethod(username, new PrivateKeyFile(keyStream, passphrase)));
        }

        if (!string.IsNullOrWhiteSpace(password)) methods.Add(new PasswordAuthenticationMethod(username, password));

        var connection = new ConnectionInfo(host, port, username, [.. methods]) { Timeout = timeout };
        using var client = new SftpClient(connection) { OperationTimeout = timeout };
        var expected = fingerprint?.Trim().Replace("SHA256:", string.Empty, StringComparison.OrdinalIgnoreCase).TrimEnd('=');
        client.HostKeyReceived += (_, e) =>
            e.CanTrust = acceptAny || string.Equals(e.FingerPrintSHA256?.TrimEnd('='), expected, StringComparison.Ordinal);
        await Task.Run(client.Connect, cancellationToken).ConfigureAwait(false);

        try
        {
            if (!client.Exists(remotePath)) throw new ArgumentException($"Remote SFTP path '{remotePath}' was not found.");
            if (!string.IsNullOrWhiteSpace(tempPath) && !client.Exists(tempPath))
                throw new ArgumentException($"Remote SFTP temp path '{tempPath}' was not found.");

            foreach (var payload in batch.Payloads)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = RemoteDeliverNaming.ResolveFileName(nameTemplate, context, payload);
                var target = RemoteDeliverNaming.Combine(remotePath, name);
                var exists = client.Exists(target);
                if (exists && !overwrite && !append)
                    throw new IOException($"Remote SFTP file '{target}' already exists and overwrite is disabled.");

                var bytes = payload.Content.ToArray();
                if (append)
                {
                    await using var remote = client.Open(target, FileMode.Append, FileAccess.Write);
                    await remote.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(tempPath))
                {
                    using var stream = new MemoryStream(bytes);
                    client.UploadFile(stream, target, canOverride: true);
                    continue;
                }

                var temp = RemoteDeliverNaming.Combine(tempPath, name + "." + context.ExecutionId + ".tmp");
                using (var stream = new MemoryStream(bytes)) client.UploadFile(stream, temp, canOverride: true);
                if (exists) client.DeleteFile(target);
                client.RenameFile(temp, target);
            }
        }
        finally
        {
            client.Disconnect();
        }
    }
}
