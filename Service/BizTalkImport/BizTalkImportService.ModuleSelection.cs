using System.Xml;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static string SelectFetchModule(string transportType, string address)
    {
        var normalized = $"{transportType} {address}".ToLowerInvariant();
        if (normalized.Contains("sftp") || normalized.StartsWith("sftp://", StringComparison.Ordinal))
        {
            return "sftp-fetch";
        }

        if (normalized.Contains("wcf") || normalized.Contains("http") || normalized.StartsWith("http://", StringComparison.Ordinal) || normalized.StartsWith("https://", StringComparison.Ordinal))
        {
            return "http-inbound-fetch";
        }

        if (normalized.Contains("file") || LooksLikeFilePath(address))
        {
            return "file-system-fetch";
        }

        return "document-repository-fetch";
    }

    private static string SelectDeliverModule(string transportType, string address)
    {
        var normalized = $"{transportType} {address}".ToLowerInvariant();
        if (normalized.Contains("smtp") || normalized.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return "smtp-email-deliver";
        }

        if (normalized.Contains("file") || LooksLikeFilePath(address))
        {
            return "file-system-deliver";
        }

        if (normalized.Contains("http") || normalized.Contains("wcf") || normalized.StartsWith("http://", StringComparison.Ordinal) || normalized.StartsWith("https://", StringComparison.Ordinal))
        {
            return "http-deliver";
        }

        return "document-repository-store";
    }

    private static string SelectParseModule(BizTalkProjectResponse project)
    {
        if (ProjectHasConfiguredEnvelope(project))
        {
            return "xml-envelope-debatch-parse";
        }

        if (LooksLikeHl7(project))
        {
            return "hl7-parse";
        }

        if (LooksLikeFlatFile(project))
        {
            return "flat-file-parse";
        }

        if (project.SchemaCount > 0 || AllArtifacts(project).Any(static artifact => string.Equals(artifact.Kind, "Schema", StringComparison.OrdinalIgnoreCase)))
        {
            return "xml-xsd-parse";
        }

        return "json-parse";
    }

    private static readonly System.Text.RegularExpressions.Regex EnvelopeSpecNamesPattern = new(
        """<Property\s+Name="EnvelopeSpecNames">\s*<Value[^>]*>([^<]+)</Value>""",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.Singleline);

    private static bool ProjectHasConfiguredEnvelope(BizTalkProjectResponse project)
    {
        var pipelineFiles = AllArtifacts(project)
            .Where(static artifact => string.Equals(artifact.Kind, "Pipeline", StringComparison.OrdinalIgnoreCase))
            .Select(static artifact => artifact.FullPath);

        foreach (var pipelineFile in pipelineFiles)
        {
            if (!File.Exists(pipelineFile))
            {
                continue;
            }

            var content = File.ReadAllText(pipelineFile);
            var match = EnvelopeSpecNamesPattern.Match(content);
            if (match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value))
            {
                return true;
            }
        }

        return false;
    }

    private static string SelectRenderModule(BizTalkProjectResponse project, SendPortAnalysis? sendPort)
    {
        if (IsSoapSendPort(sendPort))
        {
            return "soap-envelope-render";
        }

        if (LooksLikeHl7(project))
        {
            return "hl7-render";
        }

        if (project.MapCount > 0)
        {
            return "xslt-render";
        }

        if (LooksLikeFlatFile(project))
        {
            return "flat-file-render";
        }

        if (project.SchemaCount > 0)
        {
            return "xml-render";
        }

        return "json-render";
    }

    private static IReadOnlyList<SendPortAnalysis> ConsolidateSendPorts(IReadOnlyList<SendPortAnalysis> sendPorts, List<string> draftWarnings)
    {
        var consolidated = new List<SendPortAnalysis>(sendPorts.Count);
        foreach (var group in sendPorts.GroupBy(CreateSendPortEquivalenceKey, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1 || !group.All(IsSoapSendPort))
            {
                consolidated.AddRange(group);
                continue;
            }

            var preferred = group
                .OrderByDescending(static sendPort => sendPort.TransportType.Contains("custom", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(static sendPort => sendPort.TransportProperties.Count)
                .ThenBy(static sendPort => sendPort.Name, StringComparer.OrdinalIgnoreCase)
                .First();
            consolidated.Add(preferred);

            var alternateNames = group.Where(sendPort => !ReferenceEquals(sendPort, preferred)).Select(static sendPort => sendPort.Name).ToArray();
            draftWarnings.Add($"Equivalent send ports {string.Join(", ", group.Select(static sendPort => sendPort.Name))} were consolidated into '{preferred.Name}' because they target the same SOAP endpoint and actions. Review alternate bindings ({string.Join(", ", alternateNames)}) before cutover.");
        }

        return consolidated;
    }

    private static string CreateSendPortEquivalenceKey(SendPortAnalysis sendPort)
    {
        var actionSignature = string.Join("|", ParseSoapActionMappings(sendPort)
            .OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static entry => $"{entry.Key}={entry.Value}"));
        return string.Join("|",
            sendPort.Address.Trim(),
            sendPort.TransmitPipeline.Trim(),
            sendPort.ReceivePipeline.Trim(),
            actionSignature);
    }

    private static bool LooksLikeHl7(BizTalkProjectResponse project)
        => project.Name.Contains("hl7", StringComparison.OrdinalIgnoreCase)
            || AllArtifacts(project).Any(static artifact => artifact.Name.Contains("hl7", StringComparison.OrdinalIgnoreCase));

    private static bool IsSoapSendPort(SendPortAnalysis? sendPort)
    {
        if (sendPort is null)
        {
            return false;
        }

        var transportType = sendPort.TransportType;
        return transportType.Contains("wcf", StringComparison.OrdinalIgnoreCase)
            || transportType.Contains("soap", StringComparison.OrdinalIgnoreCase)
            || ParseSoapActionMappings(sendPort).Count > 0;
    }

    private static string ResolveSoapVersion(SendPortAnalysis? sendPort)
    {
        if (sendPort is null)
        {
            return "1.2";
        }

        var bindingType = TryGetTransportProperty(sendPort.TransportProperties, "BindingType") ?? string.Empty;
        var transportType = sendPort.TransportType;
        if (bindingType.Contains("basicHttp", StringComparison.OrdinalIgnoreCase)
            || transportType.Contains("basic", StringComparison.OrdinalIgnoreCase))
        {
            return "1.1";
        }

        return "1.2";
    }

    private static IReadOnlyDictionary<string, string> ParseSoapActionMappings(SendPortAnalysis? sendPort)
    {
        var staticAction = sendPort is null ? null : TryGetTransportProperty(sendPort.TransportProperties, "StaticAction");
        if (string.IsNullOrWhiteSpace(staticAction))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var document = XDocument.Parse(staticAction, LoadOptions.None);
            return document.Descendants()
                .Where(static element => string.Equals(element.Name.LocalName, "Operation", StringComparison.Ordinal))
                .Select(static element => new
                {
                    Name = element.Attribute("Name")?.Value,
                    Action = element.Attribute("Action")?.Value
                })
                .Where(static element => !string.IsNullOrWhiteSpace(element.Name) && !string.IsNullOrWhiteSpace(element.Action))
                .GroupBy(static element => element.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(static group => group.Key, static group => group.Last().Action!, StringComparer.OrdinalIgnoreCase);
        }
        catch (XmlException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool LooksLikeFlatFile(BizTalkProjectResponse project)
        => project.Name.Contains("flat", StringComparison.OrdinalIgnoreCase)
            || project.Name.Contains("csv", StringComparison.OrdinalIgnoreCase)
            || AllArtifacts(project).Any(static artifact => artifact.Name.Contains("flat", StringComparison.OrdinalIgnoreCase)
                || artifact.Name.Contains("csv", StringComparison.OrdinalIgnoreCase)
                || artifact.Name.Contains("delim", StringComparison.OrdinalIgnoreCase));

    private static string InferFlatFileDelimiter(BizTalkProjectResponse project)
    {
        if (project.Name.Contains("pipe", StringComparison.OrdinalIgnoreCase))
        {
            return "Pipe";
        }

        if (project.Name.Contains("tab", StringComparison.OrdinalIgnoreCase))
        {
            return "Tab";
        }

        if (project.Name.Contains("semi", StringComparison.OrdinalIgnoreCase))
        {
            return "Semicolon";
        }

        return "Comma";
    }

    /// <summary>
    /// Resolves the content type a rendered payload should be delivered with, derived from the render module's
    /// own output extension via the shared <see cref="ContentTypeResolver"/> so this never drifts from the
    /// extension-to-content-type mapping every file-based module already agrees on.
    /// </summary>
    private static string ResolveContentType(string renderModule)
        => ContentTypeResolver.FromFileName($"payload{ResolveExtension(renderModule)}");

    private static string ResolveExtension(string renderModule)
    {
        return renderModule switch
        {
            "hl7-render" => ".hl7",
            "flat-file-render" => ".txt",
            "json-render" => ".json",
            _ => ".xml"
        };
    }
}
