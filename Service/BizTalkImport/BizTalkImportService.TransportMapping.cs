using System.Text.RegularExpressions;
using Mulse.Modules;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static bool IsNsoftwareSftp(string transportType, string address)
        => transportType.Contains("sftp", StringComparison.OrdinalIgnoreCase) || address.StartsWith("sftp://", StringComparison.OrdinalIgnoreCase);

    private static bool IsNsoftwareFtp(string transportType, string address)
        => (transportType.Contains("ftp", StringComparison.OrdinalIgnoreCase) && !transportType.Contains("sftp", StringComparison.OrdinalIgnoreCase))
            || address.StartsWith("ftp://", StringComparison.OrdinalIgnoreCase)
            || address.StartsWith("ftps://", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(string? value)
        => value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("1", StringComparison.Ordinal));

    /// <summary>Finds a flattened adapter property by exact key, then by dotted-key suffix (e.g. "AcceptAny").</summary>
    private static string? FindProperty(IReadOnlyDictionary<string, string> properties, string name)
    {
        if (TryGetTransportProperty(properties, name) is { } exact)
        {
            return exact;
        }

        return properties.FirstOrDefault(pair => pair.Key.EndsWith("." + name, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(pair.Value)).Value;
    }

    /// <summary>BizTalk binding variables look like <c>%SSHHost%</c> and are resolved per environment, so they become configuration requirements.</summary>
    private static bool IsBindingVariable(string value)
        => BindingVariablePattern().IsMatch(value);

    [GeneratedRegex("%[A-Za-z_][A-Za-z0-9_]*%", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BindingVariablePattern();

    /// <summary>
    /// Translates BizTalk file-name macros into Mulse naming tokens. Returns the translated value and whether any
    /// macro could not be translated faithfully (those need a manual review).
    /// </summary>
    private static (string Value, bool Approximated) TranslateMacros(string value)
    {
        var approximated = false;
        var translated = BindingVariablePattern().Replace(value, match =>
        {
            switch (match.Value.ToLowerInvariant())
            {
                case "%sourcefilename%":
                    return "{{payloadName}}";
                case "%messageid%":
                    approximated = true;
                    return "{{executionId}}";
                default:
                    approximated = true;
                    return match.Value;
            }
        });
        return (translated, approximated);
    }

    private static string ConfigOrPlaceholder(
        List<BizTalkSettingRequirementResponse>? requirements,
        string referencePrefix,
        string settingPath,
        string? value,
        string description)
    {
        return string.IsNullOrWhiteSpace(value) || IsBindingVariable(value)
            ? AddRequirement(requirements, referencePrefix, settingPath, "Config", description, appliedToDraft: true)
            : ResolveConfigValue(requirements, referencePrefix, settingPath, value, description, includeConfigPlaceholders: true);
    }

    private static void MapRemoteDeliver(
        string module,
        SendPortAnalysis sendPort,
        string referencePrefix,
        string settingPathPrefix,
        List<BizTalkSettingRequirementResponse>? requirements,
        List<string>? warnings,
        Dictionary<string, string> settings)
    {
        var isSftp = string.Equals(module, "sftp-deliver", StringComparison.Ordinal);
        var label = isSftp ? "SFTP" : "FTP";
        var props = sendPort.TransportProperties;
        var port = FindProperty(props, isSftp ? "SSHPort" : "FTPPort");

        settings["host"] = ConfigOrPlaceholder(requirements, referencePrefix, $"{settingPathPrefix}.host", FindProperty(props, isSftp ? "SSHHost" : "FTPServer"), $"{label} host for send port '{sendPort.Name}'.");
        settings["port"] = !string.IsNullOrWhiteSpace(port) && !IsBindingVariable(port) ? port : (isSftp ? "22" : "21");
        settings["username"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.username", "Secret", $"{label} user name for send port '{sendPort.Name}'.", appliedToDraft: true);
        settings["password"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.password", "Secret", $"{label} password for send port '{sendPort.Name}' (BizTalk never exports passwords).", appliedToDraft: true);
        settings["remotePath"] = ConfigOrPlaceholder(requirements, referencePrefix, $"{settingPathPrefix}.remotePath", FindProperty(props, "RemotePath") ?? "/", $"Remote directory for send port '{sendPort.Name}'.");

        var remoteFile = FindProperty(props, "RemoteFile");
        if (!string.IsNullOrWhiteSpace(remoteFile))
        {
            var (translated, approximated) = TranslateMacros(remoteFile);
            settings["remoteFileName"] = IsBindingVariable(translated)
                ? AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.remoteFileName", "Config", $"Remote file name template for send port '{sendPort.Name}'. Tokens: {{{{payloadName}}}}, {{{{executionId}}}}, {{{{flowId}}}}, {{{{metadata.key}}}}.", appliedToDraft: true)
                : translated;
            if (approximated)
            {
                warnings?.Add($"Send port '{sendPort.Name}' uses the file-name macro '{remoteFile}'. BizTalk macros were mapped to Mulse tokens where possible (%SourceFileName% -> {{{{payloadName}}}}, %MessageID% -> {{{{executionId}}}}); review '{settingPathPrefix}.remoteFileName'.");
            }
        }

        if (FindProperty(props, "RemoteTempPath") is { } tempPath)
        {
            settings["remoteTempPath"] = ConfigOrPlaceholder(requirements, referencePrefix, $"{settingPathPrefix}.remoteTempPath", tempPath, $"Temporary upload directory for send port '{sendPort.Name}'.");
        }

        settings["overwrite"] = IsTrue(FindProperty(props, "Overwrite")) ? "true" : "false";
        settings["append"] = IsTrue(FindProperty(props, "Append")) ? "true" : "false";
        if (int.TryParse(FindProperty(props, "Timeout"), out var timeout) && timeout > 0)
        {
            settings["timeoutSeconds"] = timeout.ToString();
        }

        if (isSftp)
        {
            settings["acceptAnyHostKey"] = "false";
            settings["hostKeyFingerprint"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.hostKeyFingerprint", "Config", $"SHA256 host key fingerprint (base64) for send port '{sendPort.Name}'. Required because Mulse does not accept unknown host keys by default.", appliedToDraft: true);
            if (IsTrue(FindProperty(props, "AcceptAny")))
            {
                warnings?.Add($"Send port '{sendPort.Name}' accepted ANY SFTP host key in BizTalk. Mulse requires a pinned fingerprint; set 'hostKeyFingerprint' or explicitly enable 'acceptAnyHostKey' in the flow UI.");
            }

            var authMode = FindProperty(props, "SSHAuthMode");
            warnings?.Add($"SFTP send port '{sendPort.Name}' uses authentication mode '{authMode ?? "unknown"}'. Password authentication was assumed; if the source used a private key, set 'privateKey' (and 'privateKeyPassphrase') in the flow UI. Credentials are never exported by BizTalk.");
        }
        else
        {
            var startMode = FindProperty(props, "SSLStartMode");
            var implicitTls = port == "990";
            settings["encryptionMode"] = implicitTls ? "Implicit" : "Auto";
            settings["passive"] = FindProperty(props, "Passive") is { } passive ? (IsTrue(passive) ? "true" : "false") : "true";
            settings["acceptAnyCertificate"] = "false";
            if (IsTrue(FindProperty(props, "AcceptAny")))
            {
                warnings?.Add($"FTP send port '{sendPort.Name}' accepted ANY server certificate in BizTalk. Mulse validates certificates by default; enable 'acceptAnyCertificate' in the flow UI only if required.");
            }

            warnings?.Add($"FTP send port '{sendPort.Name}' had SSLStartMode '{startMode ?? "unset"}'; the nsoftware value could not be mapped with certainty, so 'encryptionMode' was set to '{settings["encryptionMode"]}' (port 990 = Implicit, otherwise Auto). Verify it in the flow UI.");
        }
    }

    private static void MapSmtpDeliver(
        SendPortAnalysis sendPort,
        string referencePrefix,
        string settingPathPrefix,
        List<BizTalkSettingRequirementResponse>? requirements,
        bool includeConfigPlaceholders,
        Dictionary<string, string> settings)
    {
        var props = sendPort.TransportProperties;
        var hostSetting = TryGetTransportProperty(props, "SMTPHost") ?? TryGetTransportProperty(props, "SMTPServer");
        string? port = TryGetTransportProperty(props, "SMTPServerPort");
        var host = hostSetting;
        if (host is not null)
        {
            var separator = host.LastIndexOf(':');
            if (separator > 0 && int.TryParse(host[(separator + 1)..], out _))
            {
                port ??= host[(separator + 1)..];
                host = host[..separator];
            }
        }

        host ??= ExtractHost(sendPort.Address);
        settings["host"] = string.IsNullOrWhiteSpace(host)
            ? AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.host", "Config", "Enter the SMTP host for the migrated send port.", appliedToDraft: includeConfigPlaceholders)
            : ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.host", host, "SMTP host imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
        settings["port"] = port ?? "25";
        if (port is "587" or "465")
        {
            settings["enableSsl"] = "true";
        }

        settings["from"] = MapAddressSetting(props, "From", requirements, referencePrefix, $"{settingPathPrefix}.from", "SMTP sender address for the migrated send port.", includeConfigPlaceholders);
        settings["to"] = MapAddressSetting(props, "To", requirements, referencePrefix, $"{settingPathPrefix}.to", "SMTP recipient addresses for the migrated send port.", includeConfigPlaceholders);
        if (TryGetTransportProperty(props, "CC") is { } cc)
        {
            settings["cc"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.cc", cc, "SMTP Cc recipients imported from BizTalk.", includeConfigPlaceholders);
        }

        settings["subject"] = TryGetTransportProperty(props, "Subject") ?? $"Imported BizTalk delivery {sendPort.Name}";
        settings["attachPayload"] = "true";

        var fileNameProperty = sendPort.Operational.PipelineComponents
            .Where(static component => component.ClassName.EndsWith("SetProperty", StringComparison.OrdinalIgnoreCase))
            .Select(static component => component.Properties)
            .FirstOrDefault(static properties => string.Equals(properties.GetValueOrDefault("PropertyName"), "FileName", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(properties.GetValueOrDefault("PropertyNamespace"))
                && !string.IsNullOrEmpty(properties.GetValueOrDefault("PropertyValue")));
        if (fileNameProperty is not null)
        {
            settings["attachmentNameMetadataKey"] = "{" + fileNameProperty["PropertyNamespace"] + "}FileName";
        }
        if (TryGetTransportProperty(props, "Username") is not null || IsTrue(TryGetTransportProperty(props, "SMTPAuthenticate")))
        {
            settings["username"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.username", "Secret", "SMTP user name for the migrated send port.", appliedToDraft: true);
            settings["password"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.password", "Secret", "SMTP password for the migrated send port (BizTalk never exports passwords).", appliedToDraft: true);
        }
    }

    private static string MapAddressSetting(
        IReadOnlyDictionary<string, string> props,
        string key,
        List<BizTalkSettingRequirementResponse>? requirements,
        string referencePrefix,
        string settingPath,
        string description,
        bool includeConfigPlaceholders)
    {
        var value = TryGetTransportProperty(props, key);
        return value is null
            ? AddRequirement(requirements, referencePrefix, settingPath, "Config", $"Set the {description}", appliedToDraft: includeConfigPlaceholders)
            : ResolveConfigValue(requirements, referencePrefix, settingPath, value, $"{description} Imported from BizTalk.", includeConfigPlaceholders);
    }

    private static void MapFileDeliver(
        SendPortAnalysis sendPort,
        string referencePrefix,
        string settingPathPrefix,
        List<BizTalkSettingRequirementResponse>? requirements,
        List<string>? warnings,
        bool includeConfigPlaceholders,
        Dictionary<string, string> settings)
    {
        var props = sendPort.TransportProperties;
        var fileName = TryGetTransportProperty(props, "FileName");
        var directory = sendPort.Address;
        if (fileName is null)
        {
            var separator = Math.Max(directory.LastIndexOf('\\'), directory.LastIndexOf('/'));
            var last = separator >= 0 ? directory[(separator + 1)..] : string.Empty;
            if (separator >= 0 && (last.Contains('%', StringComparison.Ordinal) || last.Contains('*', StringComparison.Ordinal)))
            {
                fileName = last;
                directory = directory[..separator];
            }
        }

        settings["path"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.path", directory, "File-system destination imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);

        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var withoutMacro = BindingVariablePattern().Replace(fileName, string.Empty).Trim('_', '-', '.', ' ');
            var hasMacro = IsBindingVariable(fileName);
            var stem = Path.GetFileNameWithoutExtension(withoutMacro);
            var extension = Path.GetExtension(fileName);
            if (hasMacro)
            {
                settings["namingMode"] = "Random";
                if (!string.IsNullOrWhiteSpace(stem))
                {
                    settings["fileName"] = stem;
                }

                warnings?.Add($"Send port '{sendPort.Name}' names files '{fileName}'. BizTalk macros have no direct file-system-deliver equivalent; naming mode 'Random' with base name '{stem}' was used (unique name per message). Change 'namingMode'/'fileName' in the flow UI if a different convention is required.");
            }
            else
            {
                settings["namingMode"] = "Fixed";
                settings["fileName"] = fileName;
            }

            if (!string.IsNullOrWhiteSpace(extension))
            {
                settings["extension"] = extension;
            }
        }

        var copyMode = TryGetTransportProperty(props, "CopyMode");
        settings["overwriteExisting"] = copyMode == "2" ? "true" : "false";
        if (copyMode == "0")
        {
            warnings?.Add($"Send port '{sendPort.Name}' used CopyMode=0 (append to existing file). file-system-deliver cannot append; each message is written as a new file.");
        }
    }

    private static void MapFileFetch(
        ReceiveLocationAnalysis location,
        string referencePrefix,
        bool includeConfigPlaceholders,
        List<BizTalkSettingRequirementResponse>? requirements,
        string settingPathPrefix,
        Dictionary<string, string> settings)
    {
        var address = location.Address;
        var mask = TryGetTransportProperty(location.TransportProperties, "FileMask");
        var separator = Math.Max(address.LastIndexOf('\\'), address.LastIndexOf('/'));
        if (separator >= 0 && address[(separator + 1)..].IndexOfAny(['*', '?']) >= 0)
        {
            mask ??= address[(separator + 1)..];
            address = address[..separator];
        }

        settings["path"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.path", address, "File-system fetch path imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
        settings["searchPattern"] = string.IsNullOrWhiteSpace(mask) ? "*.*" : mask;
        settings["afterProcessing"] = nameof(FetchPostProcessingAction.MoveToArchive);
    }

    /// <summary>Reports send-port behavior that Mulse cannot import automatically so reviewers can configure or accept it.</summary>
    private static void AddSendPortOperationalWarnings(SendPortAnalysis sendPort, List<string> warnings)
    {
        var op = sendPort.Operational;
        var name = sendPort.Name;
        if (op.SecondaryTransportType.Length > 0 || op.SecondaryAddress.Length > 0)
        {
            warnings.Add($"Send port '{name}' has a BizTalk backup (secondary) transport ({op.SecondaryTransportType} '{op.SecondaryAddress}') used on primary failure. Mulse has no automatic failover; add a second delivery action for the backup destination in the flow UI.");
        }

        if (op.OrderedDelivery)
        {
            warnings.Add($"Send port '{name}' requires ordered delivery. Mulse delivers per batch without a strict ordering guarantee across runs; review concurrency settings.");
        }

        if (op.StopSendingOnFailure)
        {
            warnings.Add($"Send port '{name}' stops sending on failure. Configure the flow's retry/disable behavior in the UI to match.");
        }

        if (op.RouteFailedMessage)
        {
            warnings.Add($"Send port '{name}' routes failed messages to a failed-message subscriber. Use the flow's failure handling (onFailure/dead-letter) in the UI to replace it.");
        }

        if (op.ServiceWindowEnabled)
        {
            warnings.Add($"Send port '{name}' has a service window ({op.ServiceWindowFrom}-{op.ServiceWindowTo}). Mulse has no send windows; configure an interval trigger schedule instead.");
        }

        if (op.DeliveryNotification.Length > 0 && !string.Equals(op.DeliveryNotification, "1", StringComparison.Ordinal))
        {
            warnings.Add($"Send port '{name}' requests delivery notification ('{op.DeliveryNotification}'); NACK/ACK handling is not imported.");
        }

        if (op.HasSendPipelineConfiguration || op.HasReceivePipelineConfiguration)
        {
            warnings.Add($"Send port '{name}' configures pipeline component properties that are not imported: {string.Join("; ", op.PipelineProperties.Take(5))}. Recreate them with render/augment steps.");
        }

        if (op.HasInboundTransforms || op.HasOutboundTransforms)
        {
            warnings.Add($"Send port '{name}' has port-level maps. Port maps are not imported automatically; add a transform step in the flow UI.");
        }
    }

    /// <summary>Reports receive-location behavior that is not imported so it can be configured in the UI.</summary>
    private static void AddReceiveLocationOperationalWarnings(ReceiveLocationAnalysis location, List<string> warnings)
    {
        var op = location.Operational;
        if (!op.Enabled)
        {
            warnings.Add($"Receive location '{location.Name}' is disabled in BizTalk; the imported flow stays disabled and should only be enabled if the interface is still required.");
        }

        if (op.ServiceWindowEnabled)
        {
            warnings.Add($"Receive location '{location.Name}' has a service window ({op.ServiceWindowFrom}-{op.ServiceWindowTo}); configure an equivalent trigger schedule in the flow UI.");
        }

        if (op.ScheduleRecurrenceType.Length > 0 && !string.Equals(op.ScheduleRecurrenceType, "None", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"Receive location '{location.Name}' uses a '{op.ScheduleRecurrenceType}' schedule that is not imported; configure the trigger in the flow UI.");
        }

        if (op.HasPipelineConfiguration)
        {
            warnings.Add($"Receive location '{location.Name}' configures pipeline component properties that are not imported; review parse steps.");
        }
    }

    /// <summary>Converts a BizTalk send-port filter (<c>Group/Statement</c>) into Mulse <c>conditionJson</c> when every statement is supported.</summary>
    private static string? TryCreateFilterConditionJson(string filterExpression, out IReadOnlyList<string> unsupported)
    {
        var problems = new List<string>();
        unsupported = problems;
        if (string.IsNullOrWhiteSpace(filterExpression))
        {
            return null;
        }

        System.Xml.Linq.XDocument document;
        try
        {
            document = System.Xml.Linq.XDocument.Parse(filterExpression);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }

        var groups = document.Descendants().Where(static element => element.Name.LocalName == "Group").ToArray();
        if (groups.Length > 1)
        {
            problems.Add("OR groups are not supported");
            return null;
        }

        var conditions = new System.Text.Json.Nodes.JsonArray();
        foreach (var statement in document.Descendants().Where(static element => element.Name.LocalName == "Statement"))
        {
            var property = statement.Attribute("Property")?.Value ?? string.Empty;
            var operatorCode = statement.Attribute("Operator")?.Value ?? string.Empty;
            var value = statement.Attribute("Value")?.Value ?? string.Empty;
            var op = operatorCode switch
            {
                "0" => "Equals",
                "5" => "NotEquals",
                "6" => "Exists",
                _ => null
            };
            if (op is null)
            {
                problems.Add($"{property} operator {operatorCode}");
                continue;
            }

            conditions.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["source"] = "Metadata",
                ["path"] = property,
                ["operator"] = op,
                ["value"] = value
            });
        }

        return problems.Count > 0 || conditions.Count == 0 ? null : conditions.ToJsonString();
    }
}
