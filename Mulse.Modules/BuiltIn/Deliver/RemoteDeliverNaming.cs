using System.Text.RegularExpressions;

namespace Mulse.Modules.BuiltIn.Deliver;

/// <summary>Shared remote file-name templating and validation for the FTP/SFTP deliver modules.</summary>
internal static partial class RemoteDeliverNaming
{
    [GeneratedRegex(@"\{\{metadata\.([^}]+)\}\}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MetadataToken();

    public static string ResolveFileName(string? template, FlowExecutionContext context, IntegrationPayload payload)
    {
        var resolved = string.IsNullOrWhiteSpace(template) ? payload.Name : template
            .Replace("{{flowId}}", context.FlowId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{executionId}}", context.ExecutionId, StringComparison.OrdinalIgnoreCase)
            .Replace("{{payloadName}}", payload.Name, StringComparison.OrdinalIgnoreCase);
        resolved = MetadataToken().Replace(resolved, match => payload.Metadata.TryGetValue(match.Groups[1].Value, out var value) ? value : string.Empty);
        if (string.IsNullOrWhiteSpace(resolved) || resolved is "." or ".."
            || resolved.IndexOfAny(['/', '\\', '\0']) >= 0)
        {
            throw new ArgumentException($"Remote file name '{resolved}' is empty or contains a directory separator. Use a plain file name; set the directory in Remote path.");
        }

        return resolved;
    }

    public static string Combine(string directory, string fileName)
    {
        var trimmed = (directory ?? string.Empty).Replace('\\', '/').TrimEnd('/');
        return trimmed.Length == 0 ? "/" + fileName : trimmed + "/" + fileName;
    }
}
