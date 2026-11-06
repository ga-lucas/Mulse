using System.Text.Json.Nodes;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Pipeline;

namespace Mulse.CompatibilityPack;

/// <summary>
/// Pure, deterministic extraction of one part of a delivery address, replacing the hand-written <c>Substring/IndexOf</c> address parsing
/// that orchestrations use to drive dynamic send ports. Anything that does not match the selected format fails explicitly.
/// </summary>
public sealed class AddressPartTransformModule : ITransformModule
{
    public ModuleDescriptor Descriptor { get; } = new("address-part-transform", "Address part", ModuleKind.Transform,
        "Extracts one part of a delivery address. Url format is scheme://host[:port]/directory/file (user-info is rejected). Path format is a file path split on the last '\\' or '/'. "
        + "MailAddress strips a leading 'mailto:'. Invalid input fails; a missing port uses the configured default port, or fails when none is configured.",
        [
            new("format", "Address format", "Url: scheme://host[:port]/dir/file. Path: drive or UNC file path. Mail: optional mailto: prefix.", true, ModuleSettingInputKind.Select, "Url",
                [new("Url", "URL (scheme://host:port/dir/file)"), new("Path", "File path"), new("Mail", "Mail address")]),
            new("part", "Part to extract", "Host, Port, Directory (without the trailing separator; '/' for a root directory in URL format) and FileName; MailAddress for the Mail format.", true, ModuleSettingInputKind.Select, "Host",
                [new("Host", "Host"), new("Port", "Port"), new("Directory", "Directory"), new("FileName", "File name"), new("MailAddress", "Mail address")]),
            new("defaultPort", "Default port", "Used by the Port part when the URL has no explicit port, for example 21 for FTP or 22 for SFTP. Empty means a missing port fails.", false)
        ]);

    public Task<JsonNode?> InvokeAsync(FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (inputs.Count != 1 || inputs[0] is not JsonValue scalar || !scalar.TryGetValue<string>(out var address) || string.IsNullOrWhiteSpace(address))
            throw new ArgumentException("Address part transform requires exactly one non-empty text address.");
        var format = ModuleSettingReader.GetRequired(step.Settings, "format", Descriptor.Id);
        var part = ModuleSettingReader.GetRequired(step.Settings, "part", Descriptor.Id);
        var result = format switch
        {
            "Url" => FromUrl(address, part, ModuleSettingReader.GetOptional(step.Settings, "defaultPort")),
            "Path" => FromPath(address, part),
            "Mail" => part == "MailAddress" ? FromMail(address) : throw new ArgumentException("The Mail format supports only the MailAddress part."),
            _ => throw new ArgumentException($"Module '{Descriptor.Id}' has an unsupported address format '{format}'.")
        };
        return Task.FromResult<JsonNode?>(JsonValue.Create(result));
    }

    private static string FromMail(string address)
    {
        var value = address.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? address["mailto:".Length..] : address;
        value = value.Trim();
        if (value.Length == 0 || value.Any(char.IsControl)) throw new ArgumentException("The address contains no mail address.");
        return value;
    }

    private static string FromPath(string address, string part)
    {
        var separator = address.LastIndexOfAny(['\\', '/']);
        if (separator < 0 || separator == address.Length - 1) throw new ArgumentException("The path must contain a directory and a file name.");
        return part switch
        {
            "Directory" => address[..separator],
            "FileName" => address[(separator + 1)..],
            _ => throw new ArgumentException("The Path format supports only the Directory and FileName parts.")
        };
    }

    private static string FromUrl(string address, string part, string? defaultPort)
    {
        var schemeEnd = address.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd <= 0) throw new ArgumentException("The address must start with scheme://.");
        var rest = address[(schemeEnd + 3)..];
        var slash = rest.IndexOf('/');
        if (slash < 0) throw new ArgumentException("The address must contain a path after the host.");
        var authority = rest[..slash];
        if (authority.Length == 0 || authority.Contains('@')) throw new ArgumentException("The address must contain a host and no user-info.");
        var path = rest[slash..];
        var colon = authority.LastIndexOf(':');
        var host = colon < 0 ? authority : authority[..colon];
        var port = colon < 0 ? defaultPort ?? "" : authority[(colon + 1)..];
        if (host.Length == 0) throw new ArgumentException("The address must contain a host.");
        var lastSlash = path.LastIndexOf('/');
        switch (part)
        {
            case "Host": return host;
            case "Port":
                if (!int.TryParse(port, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number is < 1 or > 65535)
                    throw new ArgumentException("The address has no valid port and no valid default port is configured.");
                return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "Directory": return lastSlash == 0 ? "/" : path[..lastSlash];
            case "FileName":
                if (lastSlash == path.Length - 1) throw new ArgumentException("The address contains no file name.");
                return path[(lastSlash + 1)..];
            default: throw new ArgumentException("The Url format supports the Host, Port, Directory and FileName parts.");
        }
    }
}
