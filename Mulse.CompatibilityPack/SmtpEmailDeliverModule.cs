using System.Net;
using System.Net.Mail;
using System.Text.RegularExpressions;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class SmtpEmailDeliverModule : IDeliverModule
{
    private static readonly Regex MetadataTokenRegex = new(@"\{\{metadata\.([^}]+)\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("host", "SMTP host", "The SMTP server hostname.", true),
        new("port", "SMTP port", "The SMTP server port.", false, ModuleSettingInputKind.Text, "25"),
        new("enableSsl", "Enable SSL", "Uses SSL/TLS for SMTP delivery when enabled.", false, ModuleSettingInputKind.Boolean, "false"),
        new("from", "From", "The sender email address.", true),
        new("to", "To", "A semicolon or comma separated list of recipient email addresses.", true, ModuleSettingInputKind.TextArea),
        new("cc", "Cc", "Optional Cc recipients.", false, ModuleSettingInputKind.TextArea),
        new("subject", "Subject", "The outbound email subject. Supports {{flowId}}, {{executionId}}, {{payloadName}}, and {{metadata.key}} tokens.", false, ModuleSettingInputKind.Text, "Mulse delivery {{payloadName}}"),
        new("body", "Body", "The outbound email body. Supports the same tokens as the subject.", false, ModuleSettingInputKind.TextArea, "Flow {{flowId}} execution {{executionId}} delivered {{payloadName}}."),
        new("username", "Username", "Optional SMTP username.", false),
        new("password", "Password", "Optional SMTP password.", false),
        new("attachPayload", "Attach payload", "Attaches the rendered payload to the email when enabled.", false, ModuleSettingInputKind.Boolean, "true"),
        new("usePayloadAsBody", "Use payload as body", "Uses the payload text as the email body when enabled and the payload is text-like.", false, ModuleSettingInputKind.Boolean, "false")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        capabilities: [ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "smtp-email-deliver",
        "SMTP/email deliver",
        ModuleKind.Deliver,
        "Delivers rendered payloads through SMTP as email bodies and/or attachments.",
        SettingDescriptors,
        Recommendation);

    public async Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var host = ModuleSettings.GetRequired(step.Settings, "host", Descriptor.Id);
        var port = ModuleSettings.GetInt32(step.Settings, "port", 25, Descriptor.Id);
        var enableSsl = ModuleSettings.GetBoolean(step.Settings, "enableSsl", defaultValue: false, Descriptor.Id);
        var from = ModuleSettings.GetRequired(step.Settings, "from", Descriptor.Id);
        var to = ModuleSettings.GetRequired(step.Settings, "to", Descriptor.Id);
        var cc = ModuleSettings.GetOptional(step.Settings, "cc");
        var subjectTemplate = ModuleSettings.GetOptional(step.Settings, "subject") ?? "Mulse delivery {{payloadName}}";
        var bodyTemplate = ModuleSettings.GetOptional(step.Settings, "body") ?? string.Empty;
        var username = ModuleSettings.GetOptional(step.Settings, "username");
        var password = ModuleSettings.GetOptional(step.Settings, "password");
        var attachPayload = ModuleSettings.GetBoolean(step.Settings, "attachPayload", defaultValue: true, Descriptor.Id);
        var usePayloadAsBody = ModuleSettings.GetBoolean(step.Settings, "usePayloadAsBody", defaultValue: false, Descriptor.Id);

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = enableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            UseDefaultCredentials = string.IsNullOrWhiteSpace(username)
        };

        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var message = new MailMessage
            {
                From = new MailAddress(from),
                Subject = ApplyTemplate(subjectTemplate, context, payload),
                Body = ResolveBody(bodyTemplate, usePayloadAsBody, context, payload),
                IsBodyHtml = false
            };

            AddAddresses(message.To, to);
            AddAddresses(message.CC, cc);

            if (attachPayload)
            {
                var attachmentStream = new MemoryStream(payload.Content.ToArray());
                var attachment = new Attachment(attachmentStream, payload.Name, payload.ContentType);
                message.Attachments.Add(attachment);
            }

            await client.SendMailAsync(message).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ResolveBody(string template, bool usePayloadAsBody, FlowExecutionContext context, IntegrationPayload payload)
    {
        if (usePayloadAsBody && IsTextLike(payload.ContentType))
        {
            return payload.GetText();
        }

        return ApplyTemplate(template, context, payload);
    }

    private static bool IsTextLike(string contentType)
        => contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("xml", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("hl7", StringComparison.OrdinalIgnoreCase);

    private static void AddAddresses(MailAddressCollection collection, string? addresses)
    {
        if (string.IsNullOrWhiteSpace(addresses))
        {
            return;
        }

        foreach (var address in addresses.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            collection.Add(address);
        }
    }

    private static string ApplyTemplate(string template, FlowExecutionContext context, IntegrationPayload payload)
    {
        var resolved = template
            .Replace("{{flowId}}", context.FlowId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{executionId}}", context.ExecutionId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{payloadName}}", payload.Name, StringComparison.OrdinalIgnoreCase);

        return MetadataTokenRegex.Replace(resolved, match =>
        {
            var key = match.Groups[1].Value;
            return payload.Metadata.TryGetValue(key, out var value) ? value : string.Empty;
        });
    }
}
