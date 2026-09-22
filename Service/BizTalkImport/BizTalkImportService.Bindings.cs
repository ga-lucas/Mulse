using System.Xml;
using System.Xml.Linq;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static IReadOnlyList<string> DiscoverBindingFiles(string rootDirectory)
    {
        return BindingFilePatterns
            .SelectMany(pattern => Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Resolves binding files supplied out-of-band (outside the analyzed solution/directory), such as
    /// binding exports produced by the BizTalk Administration Console or a deployment framework's
    /// environment settings. Each entry may be a specific binding file or a directory to search recursively
    /// using the same patterns as <see cref="DiscoverBindingFiles"/>.
    /// </summary>
    private static IReadOnlyList<string> ResolveAdditionalBindingFiles(IReadOnlyList<string> additionalBindingPaths, List<string> warnings)
    {
        var resolved = new List<string>();
        foreach (var path in additionalBindingPaths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
            {
                resolved.Add(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                resolved.AddRange(BindingFilePatterns.SelectMany(pattern => Directory.EnumerateFiles(fullPath, pattern, SearchOption.AllDirectories)));
            }
            else
            {
                warnings.Add($"Additional binding path '{path}' was not found and was skipped.");
            }
        }

        return resolved;
    }

    private static BindingFileAnalysis AnalyzeBindingFile(string filePath)
    {
        var document = XDocument.Load(filePath, LoadOptions.None);
        if (!string.Equals(document.Root?.Name.LocalName, "BindingInfo", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The XML root element was not BindingInfo.");
        }

        var receivePorts = document.Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "ReceivePort", StringComparison.Ordinal))
            .Select(ParseReceivePort)
            .ToArray();
        var sendPorts = document.Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "SendPort", StringComparison.Ordinal))
            .Select(ParseSendPort)
            .ToArray();

        return new BindingFileAnalysis(
            Path.GetFileName(filePath),
            filePath,
            receivePorts,
            sendPorts);
    }

    private static ReceivePortAnalysis ParseReceivePort(XElement portElement)
    {
        var receiveLocations = portElement.Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "ReceiveLocation", StringComparison.Ordinal))
            .Select(ParseReceiveLocation)
            .ToArray();

        return new ReceivePortAnalysis(
            portElement.Attribute("Name")?.Value ?? "ReceivePort",
            ParseIsTwoWay(portElement),
            receiveLocations);
    }

    private static ReceiveLocationAnalysis ParseReceiveLocation(XElement locationElement)
    {
        var transport = locationElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "ReceiveHandlerTransportType", StringComparison.Ordinal))
            ?? locationElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "TransportType", StringComparison.Ordinal));
        var transportType = transport?.Attribute("Name")?.Value ?? "Unknown";
        var address = locationElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Address", StringComparison.Ordinal))?.Value ?? string.Empty;
        var receivePipeline = locationElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "ReceivePipeline", StringComparison.Ordinal))?.Attribute("Name")?.Value ?? string.Empty;
        var transportData = locationElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "TransportTypeData", StringComparison.Ordinal))?.Value;

        return new ReceiveLocationAnalysis(
            locationElement.Attribute("Name")?.Value ?? "ReceiveLocation",
            transportType,
            address,
            receivePipeline,
            ParseTransportProperties(transportData));
    }

    private static SendPortAnalysis ParseSendPort(XElement sendPortElement)
    {
        var primaryTransport = sendPortElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "PrimaryTransport", StringComparison.Ordinal));
        var transportType = primaryTransport?.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "TransportType", StringComparison.Ordinal))?.Attribute("Name")?.Value ?? "Unknown";
        var address = primaryTransport?.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Address", StringComparison.Ordinal))?.Value ?? string.Empty;
        var transmitPipeline = sendPortElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "TransmitPipeline", StringComparison.Ordinal))?.Attribute("Name")?.Value ?? string.Empty;
        var receivePipeline = sendPortElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "ReceivePipeline", StringComparison.Ordinal))?.Attribute("Name")?.Value ?? string.Empty;
        var transportData = primaryTransport?.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "TransportTypeData", StringComparison.Ordinal))?.Value;
        var filter = sendPortElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Filter", StringComparison.Ordinal));
        var retryCount = ParseNonNegativeInt(primaryTransport?.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "RetryCount", StringComparison.Ordinal))?.Value);
        var retryIntervalMinutes = ParseNonNegativeInt(primaryTransport?.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "RetryInterval", StringComparison.Ordinal))?.Value);

        return new SendPortAnalysis(
            sendPortElement.Attribute("Name")?.Value ?? "SendPort",
            sendPortElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Description", StringComparison.Ordinal))?.Value ?? string.Empty,
            transportType,
            address,
            transmitPipeline,
            receivePipeline,
            filter is null ? string.Empty : string.Concat(filter.Nodes().OfType<XText>().Select(static node => node.Value)).Trim(),
            ParseIsTwoWay(sendPortElement),
            ParseTransportProperties(transportData),
            retryCount,
            retryIntervalMinutes);
    }

    private static int ParseNonNegativeInt(string? value)
        => int.TryParse(value, out var parsed) && parsed >= 0 ? parsed : 0;

    private static bool ParseIsTwoWay(XElement portOrSendPortElement)
        => bool.TryParse(portOrSendPortElement.Attribute("IsTwoWay")?.Value, out var isTwoWay) && isTwoWay;

    private static IReadOnlyDictionary<string, string> ParseTransportProperties(string? transportData)
    {
        if (string.IsNullOrWhiteSpace(transportData))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var transportDocument = XDocument.Parse(transportData, LoadOptions.None);
            return transportDocument.Root?.Elements()
                .GroupBy(static element => element.Name.LocalName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Last().Value,
                    StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch (XmlException)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static BizTalkBindingFileResponse MapBindingFile(BindingFileAnalysis binding)
    {
        return new BizTalkBindingFileResponse(
            binding.Name,
            binding.FilePath,
            binding.ReceivePorts.Count,
            binding.SendPorts.Count,
            binding.ReceivePorts.Select(port => new BizTalkReceivePortResponse(
                port.Name,
                port.IsTwoWay,
                port.ReceiveLocations.Select(location => new BizTalkReceiveLocationResponse(
                    location.Name,
                    location.TransportType,
                    location.Address,
                    location.ReceivePipeline,
                    SelectFetchModule(location.TransportType, location.Address),
                    CreateFetchSettings(location, CreateSlug($"{binding.Name}-{location.Name}"), includeConfigPlaceholders: false).Settings)).ToArray())).ToArray(),
            binding.SendPorts.Select(port =>
            {
                var previewRoute = CreateRouteForSendPort(CreateSlug($"{binding.Name}-{port.Name}"), CreateDefaultProjectProfile(), port, routeIndex: 0, requirements: null, draftWarnings: []);
                return new BizTalkSendPortResponse(
                    port.Name,
                    port.Description,
                    port.TransportType,
                    port.Address,
                    port.TransmitPipeline,
                    port.ReceivePipeline,
                    port.FilterExpression,
                    port.IsTwoWay,
                    previewRoute.Render.Module,
                    previewRoute.Deliver.Module,
                    previewRoute.Render.Settings,
                    previewRoute.Deliver.Settings);
            }).ToArray());
    }

    private static bool IsBindingRelatedToProject(BindingFileAnalysis binding, BizTalkProjectResponse project)
    {
        var projectDirectory = Path.GetDirectoryName(project.ProjectPath) ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(projectDirectory) && binding.FilePath.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalizedProject = NormalizeArtifactName(project.Name);
        var normalizedBinding = NormalizeArtifactName(Path.GetFileNameWithoutExtension(binding.Name).Replace(".BindingInfo", string.Empty, StringComparison.OrdinalIgnoreCase));
        return normalizedBinding.Contains(normalizedProject, StringComparison.OrdinalIgnoreCase)
            || normalizedProject.Contains(normalizedBinding, StringComparison.OrdinalIgnoreCase);
    }
}
