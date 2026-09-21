using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules;
using Service.Models;

namespace Service;

public sealed class BizTalkImportService : IBizTalkImportService
{
    private static readonly Regex SolutionProjectPattern = new("Project\\(.*\\)\\s=\\s\"[^\"]+\",\\s\"(?<path>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex MultipleDashesPattern = new("-+", RegexOptions.Compiled);

    public BizTalkImportService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public Task<BizTalkSolutionAnalysisResponse> AnalyzeAsync(AnalyzeBizTalkSolutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedPath = Path.GetFullPath(request.SourcePath);
        var scope = ResolveScope(resolvedPath);
        if (scope.BizTalkProjects.Count == 0)
        {
            throw new InvalidOperationException($"The source '{resolvedPath}' does not contain any BizTalk project files.");
        }

        var warnings = new List<string>
        {
            "Generated draft flows are imported disabled so placeholder configuration and secrets can be reviewed before runtime use.",
            "BizTalk orchestrations are inventoried for guidance, but ODX control flow is not auto-translated yet."
        };

        var bizTalkProjects = new List<BizTalkProjectAnalysis>(scope.BizTalkProjects.Count);
        foreach (var projectPath in scope.BizTalkProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bizTalkProjects.Add(AnalyzeBizTalkProject(projectPath, scope.RootDirectory));
            }
            catch (XmlException exception)
            {
                warnings.Add($"Unable to parse BizTalk project '{projectPath}': {exception.Message}");
            }
        }

        var customAssemblies = new List<BizTalkCustomAssemblyResponse>(scope.CustomProjects.Count);
        foreach (var projectPath in scope.CustomProjects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                customAssemblies.Add(AnalyzeCustomProject(projectPath, scope.RootDirectory, bizTalkProjects));
            }
            catch (XmlException exception)
            {
                warnings.Add($"Unable to parse custom project '{projectPath}': {exception.Message}");
            }
        }

        var bindingAnalyses = new List<BindingFileAnalysis>();
        foreach (var bindingPath in DiscoverBindingFiles(scope.RootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bindingAnalyses.Add(AnalyzeBindingFile(bindingPath));
            }
            catch (XmlException exception)
            {
                warnings.Add($"Unable to parse binding file '{bindingPath}': {exception.Message}");
            }
            catch (InvalidOperationException exception)
            {
                warnings.Add($"Unable to import binding file '{bindingPath}': {exception.Message}");
            }
        }

        if (bindingAnalyses.Count == 0)
        {
            warnings.Add("No BizTalk binding files were discovered, so fetch and deliver drafts fall back to generic repository-style modules and placeholders.");
        }

        var hydratedProjects = bizTalkProjects
            .Select(project => project.Response with
            {
                ReferencedCustomAssemblies = customAssemblies
                    .Where(assembly => assembly.UsedByProjects.Contains(project.Response.Name, StringComparer.OrdinalIgnoreCase))
                    .Select(assembly => assembly.Name)
                    .ToArray()
            })
            .ToArray();

        var candidateProjects = bizTalkProjects
            .Where(project => ShouldCreateFlowCandidate(project.Response, bindingAnalyses))
            .ToArray();
        if (candidateProjects.Length == 0)
        {
            candidateProjects = bizTalkProjects.ToArray();
        }

        if (candidateProjects.Length < bizTalkProjects.Count)
        {
            warnings.Add($"{bizTalkProjects.Count - candidateProjects.Length} BizTalk support project(s) were inventoried but not turned into standalone draft flows because they look like shared schema or pipeline libraries without direct bindings, maps, or orchestrations.");
        }

        var flowCandidates = candidateProjects
            .Select(project => CreateFlowCandidate(project, customAssemblies, bindingAnalyses, scope.RootDirectory))
            .ToArray();

        return Task.FromResult(new BizTalkSolutionAnalysisResponse(
            resolvedPath,
            scope.SourceType,
            scope.DisplayName,
            scope.RootDirectory,
            bizTalkProjects.Count,
            customAssemblies.Count,
            bizTalkProjects.Sum(static project => project.Response.OrchestrationCount),
            bizTalkProjects.Sum(static project => project.Response.MapCount),
            bizTalkProjects.Sum(static project => project.Response.SchemaCount),
            bizTalkProjects.Sum(static project => project.Response.PipelineCount),
            bindingAnalyses.Select(MapBindingFile).ToArray(),
            hydratedProjects,
            customAssemblies,
            flowCandidates,
            warnings));
    }

    private static AnalysisScope ResolveScope(string sourcePath)
    {
        if (File.Exists(sourcePath))
        {
            var extension = Path.GetExtension(sourcePath);
            if (string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase))
            {
                var rootDirectory = Path.GetDirectoryName(sourcePath)
                    ?? throw new InvalidOperationException($"Unable to determine the root directory for '{sourcePath}'.");
                var projectPaths = LoadProjectsFromSolution(sourcePath, rootDirectory);
                return new AnalysisScope(
                    sourcePath,
                    "Solution",
                    Path.GetFileNameWithoutExtension(sourcePath),
                    rootDirectory,
                    projectPaths.BizTalkProjects,
                    projectPaths.CustomProjects);
            }

            if (string.Equals(extension, ".btproj", StringComparison.OrdinalIgnoreCase))
            {
                var rootDirectory = Path.GetDirectoryName(sourcePath)
                    ?? throw new InvalidOperationException($"Unable to determine the root directory for '{sourcePath}'.");
                return new AnalysisScope(sourcePath, "Project", Path.GetFileNameWithoutExtension(sourcePath), rootDirectory, [sourcePath], []);
            }

            if (string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                var rootDirectory = Path.GetDirectoryName(sourcePath)
                    ?? throw new InvalidOperationException($"Unable to determine the root directory for '{sourcePath}'.");
                return new AnalysisScope(sourcePath, "Project", Path.GetFileNameWithoutExtension(sourcePath), rootDirectory, [], [sourcePath]);
            }
        }

        if (!Directory.Exists(sourcePath))
        {
            throw new ArgumentException($"The source path '{sourcePath}' was not found.", nameof(sourcePath));
        }

        return new AnalysisScope(
            sourcePath,
            "Directory",
            new DirectoryInfo(sourcePath).Name,
            sourcePath,
            Directory.EnumerateFiles(sourcePath, "*.btproj", SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray(),
            Directory.EnumerateFiles(sourcePath, "*.csproj", SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static SolutionProjectSet LoadProjectsFromSolution(string solutionPath, string rootDirectory)
    {
        var bizTalkProjects = new List<string>();
        var customProjects = new List<string>();

        foreach (var line in File.ReadLines(solutionPath))
        {
            var match = SolutionProjectPattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var relativePath = match.Groups["path"].Value;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
            if (string.Equals(Path.GetExtension(fullPath), ".btproj", StringComparison.OrdinalIgnoreCase))
            {
                bizTalkProjects.Add(fullPath);
            }
            else if (string.Equals(Path.GetExtension(fullPath), ".csproj", StringComparison.OrdinalIgnoreCase))
            {
                customProjects.Add(fullPath);
            }
        }

        return new SolutionProjectSet(
            bizTalkProjects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            customProjects.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static BizTalkProjectAnalysis AnalyzeBizTalkProject(string projectPath, string rootDirectory)
    {
        var document = XDocument.Load(projectPath, LoadOptions.None);
        var projectName = GetProperty(document, "AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath);
        var projectDirectory = Path.GetDirectoryName(projectPath) ?? rootDirectory;
        var artifacts = new List<BizTalkArtifactResponse>();

        AddArtifacts(document, projectDirectory, rootDirectory, "XLang", "Orchestration", artifacts);
        AddArtifacts(document, projectDirectory, rootDirectory, "Transform", "Map", artifacts);
        AddArtifacts(document, projectDirectory, rootDirectory, "Schema", "Schema", artifacts);
        AddArtifacts(document, projectDirectory, rootDirectory, "Pipeline", "Pipeline", artifacts);

        var references = document.Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "Reference", StringComparison.Ordinal))
            .Select(static reference => NormalizeReferenceName(
                reference.Attribute("Include")?.Value,
                reference.Elements().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Name", StringComparison.Ordinal))?.Value))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new BizTalkProjectAnalysis(
            new BizTalkProjectResponse(
                projectName,
                projectPath,
                artifacts.Count(static artifact => string.Equals(artifact.Kind, "Orchestration", StringComparison.OrdinalIgnoreCase)),
                artifacts.Count(static artifact => string.Equals(artifact.Kind, "Map", StringComparison.OrdinalIgnoreCase)),
                artifacts.Count(static artifact => string.Equals(artifact.Kind, "Schema", StringComparison.OrdinalIgnoreCase)),
                artifacts.Count(static artifact => string.Equals(artifact.Kind, "Pipeline", StringComparison.OrdinalIgnoreCase)),
                [],
                artifacts),
            references);
    }

    private static BizTalkCustomAssemblyResponse AnalyzeCustomProject(string projectPath, string rootDirectory, IReadOnlyList<BizTalkProjectAnalysis> bizTalkProjects)
    {
        var document = XDocument.Load(projectPath, LoadOptions.None);
        var assemblyName = GetProperty(document, "AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath);
        var suggestedModuleKind = SuggestModuleKind(assemblyName);
        var migrationApproach = CreateMigrationApproach(suggestedModuleKind, assemblyName);
        var usedByProjects = bizTalkProjects
            .Where(project => project.References.Contains(assemblyName, StringComparer.OrdinalIgnoreCase)
                || project.References.Contains(Path.GetFileNameWithoutExtension(projectPath), StringComparer.OrdinalIgnoreCase))
            .Select(project => project.Response.Name)
            .ToArray();
        var projectDirectory = Path.GetDirectoryName(projectPath) ?? rootDirectory;
        var sourceFileCount = document.Descendants()
            .Count(static element => string.Equals(element.Name.LocalName, "Compile", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(element.Attribute("Include")?.Value)
                && element.Attribute("Include")!.Value.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));

        if (sourceFileCount == 0 && Directory.Exists(projectDirectory))
        {
            sourceFileCount = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories).Count();
        }

        return new BizTalkCustomAssemblyResponse(
            assemblyName,
            projectPath,
            sourceFileCount,
            suggestedModuleKind,
            migrationApproach,
            false,
            usedByProjects);
    }

    private static BizTalkFlowCandidateResponse CreateFlowCandidate(
        BizTalkProjectAnalysis project,
        IReadOnlyList<BizTalkCustomAssemblyResponse> customAssemblies,
        IReadOnlyList<BindingFileAnalysis> bindingFiles,
        string rootDirectory)
    {
        var flowId = CreateFlowId(project.Response.Name);
        var relatedAssemblies = customAssemblies
            .Where(assembly => assembly.UsedByProjects.Contains(project.Response.Name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var relatedBindings = bindingFiles
            .Where(binding => IsBindingRelatedToProject(binding, project.Response))
            .ToArray();

        var augmentGuidance = new List<string>();
        if (relatedAssemblies.Length > 0)
        {
            augmentGuidance.AddRange(relatedAssemblies.Select(assembly =>
                $"Manually migrate '{assembly.Name}' into a native Mulse {assembly.SuggestedModuleKind} module."));
        }

        if (project.Response.MapCount > 0)
        {
            augmentGuidance.Add(project.Response.MapCount == 1
                ? "Convert the BizTalk map into XSLT or an equivalent render step before enabling the draft flow."
                : $"Convert the {project.Response.MapCount} BizTalk maps into XSLT or explicit render steps and review which one belongs on each delivery route.");
        }

        var deliveryGuidance = new List<string>
        {
            relatedBindings.Length > 0
                ? "The draft deliveries were pre-populated from BizTalk send ports and still need transport-specific validation."
                : "No binding-backed send ports were found, so the draft uses a repository-style delivery placeholder."
        };

        var openQuestions = new List<string>();
        if (project.Response.OrchestrationCount > 0)
        {
            openQuestions.Add("Which orchestration branches, subscriptions, and correlations should become separate Mulse flows versus augment steps?");
        }

        if (project.Response.PipelineCount > 0)
        {
            openQuestions.Add("Which pipeline components should become parse modules, and which belong in render or deliver stages?");
        }

        if (relatedAssemblies.Length > 0)
        {
            openQuestions.Add("Can the referenced custom assemblies be rewritten natively, or do any require a temporary wrapper during cutover?");
        }

        if (relatedBindings.Length == 0)
        {
            openQuestions.Add("No related BizTalk binding file was found for this project. Which transport and endpoint details should populate fetch and deliver stages?");
        }

        var requirements = new List<BizTalkSettingRequirementResponse>();
        var draftWarnings = new List<string>
        {
            "This draft is disabled by default so imported addresses, credentials, and map paths can be reviewed safely before execution."
        };

        var fetchStep = CreateFetchStep(flowId, relatedBindings, requirements, draftWarnings);
        var parseStep = CreateParseStep(flowId, project.Response, rootDirectory, requirements, draftWarnings);
        var deliveries = CreateDeliveryRoutes(flowId, project.Response, relatedBindings, requirements, draftWarnings);
        var augments = CreateAugments(flowId, project.Response, relatedAssemblies, deliveries.Count, requirements, draftWarnings);

        if (deliveries.Count > 1)
        {
            deliveryGuidance.Add("Multiple send ports were discovered, so the draft adds route metadata and per-route conditions. Review the generated route rules before enabling the flow.");
        }

        if (requirements.Count > 0)
        {
            openQuestions.Add("Which generated config and secret references should be backed by your final protected configuration source?");
        }

        var parseGuidance = parseStep.Module switch
        {
            "hl7-parse" => "The draft uses HL7 parsing because the project name or assets look HL7-specific.",
            "flat-file-parse" => "The draft uses flat-file parsing because the project appears to process delimited or flat-file content.",
            "xml-xsd-parse" => "The draft uses XML/XSD-aware parsing and preloads discovered schema paths where available.",
            _ => "The draft uses JSON parsing as a fallback and should be replaced if the inbound message shape is not JSON."
        };

        var draftFlow = new BizTalkDraftFlowResponse(
            flowId,
            false,
            new BizTalkDraftTriggerResponse(PipelineTriggerMode.OnDemand, null, false),
            fetchStep,
            parseStep,
            augments,
            deliveries,
            requirements,
            draftWarnings);

        return new BizTalkFlowCandidateResponse(
            flowId,
            project.Response.Name,
            $"{project.Response.OrchestrationCount} orchestration(s), {project.Response.MapCount} map(s), {project.Response.SchemaCount} schema(s), {project.Response.PipelineCount} pipeline(s), {relatedBindings.Sum(static binding => binding.SendPorts.Count)} send port(s).",
            relatedBindings.Length > 0
                ? "Receive and send bindings were imported into the draft flow. Review the generated transport settings and trigger strategy."
                : "No receive binding was found for this project, so the draft fetch step uses a repository-style staging inbox placeholder.",
            parseGuidance,
            augmentGuidance,
            deliveryGuidance,
            openQuestions,
            project.Response.Artifacts.Select(static artifact => artifact.Name).ToArray(),
            relatedBindings.Select(static binding => binding.Name).ToArray(),
            draftFlow);
    }

    private static bool ShouldCreateFlowCandidate(BizTalkProjectResponse project, IReadOnlyList<BindingFileAnalysis> bindingFiles)
    {
        if (project.OrchestrationCount > 0 || project.MapCount > 0)
        {
            return true;
        }

        return bindingFiles.Any(binding => IsBindingRelatedToProject(binding, project));
    }

    private static IReadOnlyList<string> DiscoverBindingFiles(string rootDirectory)
    {
        var patterns = new[] { "*.BindingInfo.xml", "*Binding*.xml" };
        return patterns
            .SelectMany(pattern => Directory.EnumerateFiles(rootDirectory, pattern, SearchOption.AllDirectories))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

        return new SendPortAnalysis(
            sendPortElement.Attribute("Name")?.Value ?? "SendPort",
            sendPortElement.Descendants().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Description", StringComparison.Ordinal))?.Value ?? string.Empty,
            transportType,
            address,
            transmitPipeline,
            receivePipeline,
            filter is null ? string.Empty : string.Concat(filter.Nodes().OfType<XText>().Select(static node => node.Value)).Trim(),
            ParseTransportProperties(transportData));
    }

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
                    previewRoute.Render.Module,
                    previewRoute.Deliver.Module,
                    previewRoute.Render.Settings,
                    previewRoute.Deliver.Settings);
            }).ToArray());
    }

    private static BizTalkDraftStepResponse CreateFetchStep(
        string flowId,
        IReadOnlyList<BindingFileAnalysis> bindings,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var location = bindings.SelectMany(static binding => binding.ReceivePorts).SelectMany(static port => port.ReceiveLocations).FirstOrDefault();
        if (location is null)
        {
            var placeholderPath = AddRequirement(requirements, flowId, "fetch.settings.path", "Config", "Choose the staging repository path used when no BizTalk receive location could be imported.", appliedToDraft: true);
            draftWarnings.Add("No BizTalk receive location was matched to this project, so the draft uses a repository inbox placeholder for fetch.");
            return new BizTalkDraftStepResponse(
                "document-repository-fetch",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["path"] = placeholderPath,
                    ["searchPattern"] = "*.*",
                    ["includeMetadataSidecars"] = "true"
                });
        }

        return CreateFetchSettings(location, flowId, includeConfigPlaceholders: true, requirements, "fetch.settings");
    }

    private static BizTalkDraftStepResponse CreateParseStep(
        string flowId,
        BizTalkProjectResponse project,
        string rootDirectory,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var module = SelectParseModule(project);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(module, "xml-xsd-parse", StringComparison.OrdinalIgnoreCase))
        {
            var schemaPaths = project.Artifacts
                .Where(static artifact => string.Equals(artifact.Kind, "Schema", StringComparison.OrdinalIgnoreCase))
                .Select(artifact => Path.GetFullPath(Path.Combine(rootDirectory, artifact.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
                .ToArray();
            if (schemaPaths.Length > 0)
            {
                settings["schemaPaths"] = string.Join(";", schemaPaths);
            }
        }
        else if (string.Equals(module, "flat-file-parse", StringComparison.OrdinalIgnoreCase))
        {
            settings["delimiter"] = InferFlatFileDelimiter(project);
            settings["hasHeader"] = "true";
        }

        if (string.Equals(module, "json-parse", StringComparison.OrdinalIgnoreCase))
        {
            draftWarnings.Add("JSON parsing was selected as a fallback. Replace it if the inbound document is XML, HL7, or flat-file content.");
        }

        return new BizTalkDraftStepResponse(module, settings);
    }

    private static IReadOnlyList<BizTalkDraftStepResponse> CreateAugments(
        string flowId,
        BizTalkProjectResponse project,
        IReadOnlyList<BizTalkCustomAssemblyResponse> relatedAssemblies,
        int deliveryRouteCount,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var augments = new List<BizTalkDraftStepResponse>();

        var needsPromotion = relatedAssemblies.Any(assembly => assembly.Name.Contains("promot", StringComparison.OrdinalIgnoreCase)
            || assembly.Name.Contains("routing", StringComparison.OrdinalIgnoreCase)
            || assembly.Name.Contains("property", StringComparison.OrdinalIgnoreCase));
        if (needsPromotion)
        {
            augments.Add(new BizTalkDraftStepResponse(
                "metadata-promotion-augment",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["promotionsJson"] = "[{\"metadataKey\":\"documentType\",\"selector\":\"$.documentType\"}]"
                }));
            draftWarnings.Add("Metadata promotion was preloaded from custom assembly heuristics. Review JSONPath or XPath selectors before using the imported flow.");
        }

        if (deliveryRouteCount > 1)
        {
            var defaultRoute = $"route-1";
            augments.Add(new BizTalkDraftStepResponse(
                "route-selection-augment",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["routeRulesJson"] = $"[{{\"metadataKey\":\"documentType\",\"equals\":\"TODO\",\"route\":\"{defaultRoute}\"}}]",
                    ["defaultRoute"] = defaultRoute,
                    ["targetMetadataKey"] = "targetRoute"
                }));
            draftWarnings.Add("Multiple BizTalk send ports were imported. The generated route-selection augment uses placeholder routing rules and must be reviewed before enabling the flow.");
        }

        if (project.MapCount > 1)
        {
            draftWarnings.Add("The project contains multiple BizTalk maps. Review which generated delivery route should use each converted map or XSLT artifact.");
        }

        return augments;
    }

    private static IReadOnlyList<BizTalkDraftDeliveryRouteResponse> CreateDeliveryRoutes(
        string flowId,
        BizTalkProjectResponse project,
        IReadOnlyList<BindingFileAnalysis> bindings,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var sendPorts = ConsolidateSendPorts(bindings.SelectMany(static binding => binding.SendPorts).ToArray(), draftWarnings);
        if (sendPorts.Count == 0)
        {
            var placeholderPath = AddRequirement(requirements, flowId, "deliveries[0].deliver.settings.path", "Config", "Choose the staging output repository path because no BizTalk send port was imported for this project.", appliedToDraft: true);
            draftWarnings.Add("No BizTalk send port was matched to this project, so the draft uses a repository outbox placeholder.");
            return
            [
                new BizTalkDraftDeliveryRouteResponse(
                    CreateRenderStepForProject(flowId, project, sendPort: null, routeIndex: 0, requirements, draftWarnings),
                    new BizTalkDraftStepResponse(
                        "document-repository-store",
                        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["path"] = placeholderPath,
                            ["writeMetadataSidecar"] = "true"
                        }))
            ];
        }

        var routes = new List<BizTalkDraftDeliveryRouteResponse>(sendPorts.Count);
        for (var index = 0; index < sendPorts.Count; index++)
        {
            routes.Add(CreateRouteForSendPort(flowId, project, sendPorts[index], index, requirements, draftWarnings));
        }

        return routes;
    }

    private static BizTalkDraftDeliveryRouteResponse CreateRouteForSendPort(
        string referencePrefix,
        BizTalkProjectResponse project,
        SendPortAnalysis sendPort,
        int routeIndex,
        List<BizTalkSettingRequirementResponse>? requirements,
        List<string> draftWarnings)
    {
        var renderStep = CreateRenderStepForProject(referencePrefix, project, sendPort, routeIndex, requirements, draftWarnings);
        var deliverSettings = CreateDeliverSettings(sendPort, referencePrefix, routeIndex, requirements, renderStep.Module, includeConfigPlaceholders: requirements is not null);

        if (routeIndex > 0)
        {
            var conditionalSettings = new Dictionary<string, string>(renderStep.Settings, StringComparer.OrdinalIgnoreCase)
            {
                ["conditionJson"] = $"[{{\"source\":\"Metadata\",\"path\":\"targetRoute\",\"operator\":\"Equals\",\"value\":\"route-{routeIndex + 1}\"}}]"
            };
            renderStep = renderStep with { Settings = conditionalSettings };
        }

        return new BizTalkDraftDeliveryRouteResponse(renderStep, deliverSettings);
    }

    private static BizTalkDraftStepResponse CreateRenderStepForProject(
        string referencePrefix,
        BizTalkProjectResponse project,
        SendPortAnalysis? sendPort,
        int routeIndex,
        List<BizTalkSettingRequirementResponse>? requirements,
        List<string> draftWarnings)
    {
        var module = SelectRenderModule(project, sendPort);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settingPrefix = $"deliveries[{routeIndex}].render.settings";

        switch (module)
        {
            case "xslt-render":
                settings["xsltPath"] = AddRequirement(requirements, referencePrefix, $"{settingPrefix}.xsltPath", "Config", "Convert the BizTalk map or BTM asset into an XSLT file and update this path before enabling the flow.", appliedToDraft: true);
                settings["outputExtension"] = ".xml";
                break;
            case "xml-render":
                settings["rootElement"] = NormalizeArtifactName(project.Name);
                settings["itemElement"] = "Item";
                break;
            case "flat-file-render":
                settings["delimiter"] = InferFlatFileDelimiter(project);
                settings["includeHeader"] = "true";
                break;
            case "soap-envelope-render":
                var soapVersion = ResolveSoapVersion(sendPort);
                settings["soapVersion"] = soapVersion;
                var actionMappings = ParseSoapActionMappings(sendPort);
                if (actionMappings.Count > 0)
                {
                    settings["actionMappingsJson"] = JsonSerializer.Serialize(actionMappings);
                    if (actionMappings.Count > 1)
                    {
                        draftWarnings.Add($"Send port '{sendPort?.Name}' exposes multiple SOAP actions. Verify the payload root elements align with the imported action mappings before enabling the flow.");
                    }
                    else
                    {
                        settings["fallbackAction"] = actionMappings.Values.First();
                    }
                }
                else
                {
                    draftWarnings.Add($"Send port '{sendPort?.Name}' looks SOAP-based, but no static action mapping could be imported. Review the SOAP action and body shape before enabling the flow.");
                }
                break;
        }

        return new BizTalkDraftStepResponse(module, settings);
    }

    private static BizTalkDraftStepResponse CreateFetchSettings(
        ReceiveLocationAnalysis location,
        string referencePrefix,
        bool includeConfigPlaceholders,
        List<BizTalkSettingRequirementResponse>? requirements = null,
        string settingPathPrefix = "fetch.settings")
    {
        var module = SelectFetchModule(location.TransportType, location.Address);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var address = location.Address;

        switch (module)
        {
            case "sftp-fetch":
                if (Uri.TryCreate(address, UriKind.Absolute, out var sftpUri))
                {
                    settings["host"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.host", sftpUri.Host, "Remote SFTP host imported from BizTalk.", includeConfigPlaceholders);
                    settings["port"] = sftpUri.IsDefaultPort ? "22" : sftpUri.Port.ToString();
                    settings["remotePath"] = string.IsNullOrWhiteSpace(sftpUri.AbsolutePath) ? "/" : sftpUri.AbsolutePath;
                    var userName = string.IsNullOrWhiteSpace(sftpUri.UserInfo) ? null : sftpUri.UserInfo.Split(':')[0];
                    if (!string.IsNullOrWhiteSpace(userName))
                    {
                        settings["username"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.username", "Secret", "SFTP username imported from BizTalk should be moved to protected configuration.", appliedToDraft: true, actualValue: userName);
                    }
                }
                else
                {
                    settings["host"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.host", "Config", "Enter the SFTP host because BizTalk did not expose a parseable address.", appliedToDraft: true);
                    settings["remotePath"] = "/";
                }

                settings.TryAdd("port", "22");
                settings.TryAdd("username", AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.username", "Secret", "Set the SFTP username in protected configuration before enabling the flow.", appliedToDraft: true));
                settings["password"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.password", "Secret", "Set the SFTP password in protected configuration before enabling the flow.", appliedToDraft: true);
                settings["searchPattern"] = "*.*";
                break;
            case "file-system-fetch":
                settings["path"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.path", address, "File-system fetch path imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["searchPattern"] = "*.*";
                break;
            default:
                settings["path"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.path", "Config", "Choose the staging repository path used to replace this BizTalk receive transport.", appliedToDraft: includeConfigPlaceholders, actualValue: address);
                settings["searchPattern"] = "*.*";
                settings["includeMetadataSidecars"] = "true";
                break;
        }

        return new BizTalkDraftStepResponse(module, settings);
    }

    private static BizTalkDraftStepResponse CreateDeliverSettings(
        SendPortAnalysis sendPort,
        string referencePrefix,
        int routeIndex,
        List<BizTalkSettingRequirementResponse>? requirements,
        string renderModule,
        bool includeConfigPlaceholders)
    {
        var module = SelectDeliverModule(sendPort.TransportType, sendPort.Address);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var settingPathPrefix = $"deliveries[{routeIndex}].deliver.settings";

        switch (module)
        {
            case "http-deliver":
                settings["url"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.url", sendPort.Address, "HTTP or WCF endpoint imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["method"] = "POST";
                if (!string.Equals(renderModule, "soap-envelope-render", StringComparison.OrdinalIgnoreCase))
                {
                    settings["contentType"] = ResolveContentType(renderModule);
                }
                break;
            case "file-system-deliver":
                settings["path"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.path", sendPort.Address, "File-system destination imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["extension"] = ResolveExtension(renderModule);
                break;
            case "smtp-email-deliver":
                var host = TryGetTransportProperty(sendPort.TransportProperties, "SMTPServer") ?? ExtractHost(sendPort.Address);
                settings["host"] = string.IsNullOrWhiteSpace(host)
                    ? AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.host", "Config", "Enter the SMTP host for the migrated send port.", appliedToDraft: includeConfigPlaceholders)
                    : ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.host", host, "SMTP host imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["port"] = TryGetTransportProperty(sendPort.TransportProperties, "SMTPServerPort") ?? "25";
                settings["from"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.from", "Config", "Set the SMTP sender address for the migrated send port.", appliedToDraft: includeConfigPlaceholders);
                settings["to"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.to", "Config", "Set the SMTP recipient addresses for the migrated send port.", appliedToDraft: includeConfigPlaceholders);
                settings["subject"] = $"Imported BizTalk delivery {sendPort.Name}";
                settings["attachPayload"] = "true";
                break;
            default:
                settings["path"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.path", "Config", "Choose a repository output path for this migrated send port.", appliedToDraft: includeConfigPlaceholders, actualValue: sendPort.Address);
                settings["writeMetadataSidecar"] = "true";
                break;
        }

        return new BizTalkDraftStepResponse(module, settings);
    }

    private static string SelectFetchModule(string transportType, string address)
    {
        var normalized = $"{transportType} {address}".ToLowerInvariant();
        if (normalized.Contains("sftp") || normalized.StartsWith("sftp://", StringComparison.Ordinal))
        {
            return "sftp-fetch";
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
        if (LooksLikeHl7(project))
        {
            return "hl7-parse";
        }

        if (LooksLikeFlatFile(project))
        {
            return "flat-file-parse";
        }

        if (project.SchemaCount > 0 || project.Artifacts.Any(static artifact => string.Equals(artifact.Kind, "Schema", StringComparison.OrdinalIgnoreCase)))
        {
            return "xml-xsd-parse";
        }

        return "json-parse";
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
            || project.Artifacts.Any(static artifact => artifact.Name.Contains("hl7", StringComparison.OrdinalIgnoreCase));

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
            || project.Artifacts.Any(static artifact => artifact.Name.Contains("flat", StringComparison.OrdinalIgnoreCase)
                || artifact.Name.Contains("csv", StringComparison.OrdinalIgnoreCase)
                || artifact.Name.Contains("delim", StringComparison.OrdinalIgnoreCase));

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

    private static void AddArtifacts(XDocument document, string projectDirectory, string rootDirectory, string itemName, string artifactKind, ICollection<BizTalkArtifactResponse> artifacts)
    {
        foreach (var includePath in document.Descendants()
            .Where(element => string.Equals(element.Name.LocalName, itemName, StringComparison.Ordinal))
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = Path.GetFullPath(Path.Combine(projectDirectory, includePath!));
            artifacts.Add(new BizTalkArtifactResponse(
                artifactKind,
                Path.GetFileName(includePath) ?? Path.GetFileName(fullPath),
                Path.GetRelativePath(rootDirectory, fullPath)));
        }
    }

    private static string? GetProperty(XDocument document, string propertyName)
    {
        return document.Descendants()
            .FirstOrDefault(element => string.Equals(element.Name.LocalName, propertyName, StringComparison.Ordinal))
            ?.Value;
    }

    private static string NormalizeReferenceName(string? includeValue, string? explicitName)
    {
        var name = !string.IsNullOrWhiteSpace(explicitName)
            ? explicitName
            : includeValue?.Split(',')[0];
        return name?.Trim() ?? string.Empty;
    }

    private static string SuggestModuleKind(string assemblyName)
    {
        var normalized = assemblyName.ToLowerInvariant();
        if (normalized.Contains("retrieve") || normalized.Contains("receive") || normalized.Contains("fetch"))
        {
            return "Fetch";
        }

        if (normalized.Contains("store") || normalized.Contains("send") || normalized.Contains("write") || normalized.Contains("dispatch"))
        {
            return "Deliver";
        }

        if (normalized.Contains("xsl") || normalized.Contains("transform") || normalized.Contains("map"))
        {
            return "Render";
        }

        if (normalized.Contains("hl7") || normalized.Contains("flatfile") || normalized.Contains("parser") || normalized.Contains("typecaster"))
        {
            return "Parse";
        }

        return "OrchestrationAugment";
    }

    private static string CreateMigrationApproach(string suggestedModuleKind, string assemblyName)
    {
        return suggestedModuleKind switch
        {
            "Fetch" => $"Rewrite '{assemblyName}' as a native fetch module so source transport and polling remain first-class Mulse stages.",
            "Parse" => $"Rewrite '{assemblyName}' as a native parse module so schema or message normalization is explicit and reusable.",
            "Render" => $"Rewrite '{assemblyName}' as a native render module so outbound document generation is separated from delivery.",
            "Deliver" => $"Rewrite '{assemblyName}' as a native deliver module so transport concerns stay isolated from rendering logic.",
            _ => $"Rewrite '{assemblyName}' as a native orchestration augment module unless deeper analysis shows it belongs in another stage."
        };
    }

    private static string CreateFlowId(string projectName)
    {
        var normalized = string.Concat(projectName
            .ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-'));
        var compact = MultipleDashesPattern.Replace(normalized, "-").Trim('-');
        return string.IsNullOrWhiteSpace(compact) ? "biztalk-imported-flow" : $"{compact}-flow";
    }

    private static string CreateSlug(string value)
    {
        var normalized = string.Concat(value
            .ToLowerInvariant()
            .Select(static character => char.IsLetterOrDigit(character) ? character : '-'));
        var compact = MultipleDashesPattern.Replace(normalized, "-").Trim('-');
        return string.IsNullOrWhiteSpace(compact) ? "migration-item" : compact;
    }

    private static string NormalizeArtifactName(string value)
        => string.Concat(value.Where(char.IsLetterOrDigit));

    private static string AddRequirement(
        List<BizTalkSettingRequirementResponse>? requirements,
        string referencePrefix,
        string settingPath,
        string kind,
        string description,
        bool appliedToDraft,
        string? actualValue = null)
    {
        var referenceName = $"{CreateSlug(referencePrefix)}.{CreateSlug(settingPath)}";
        var placeholder = $"{{{{{kind.ToLowerInvariant()}:{referenceName}}}}}";

        if (requirements is not null && !requirements.Any(requirement => string.Equals(requirement.SettingPath, settingPath, StringComparison.OrdinalIgnoreCase) && string.Equals(requirement.Kind, kind, StringComparison.OrdinalIgnoreCase)))
        {
            requirements.Add(new BizTalkSettingRequirementResponse(kind, settingPath, referenceName, placeholder, description, appliedToDraft));
        }

        return appliedToDraft || string.IsNullOrWhiteSpace(actualValue)
            ? placeholder
            : actualValue;
    }

    private static string ResolveConfigValue(
        List<BizTalkSettingRequirementResponse>? requirements,
        string referencePrefix,
        string settingPath,
        string? actualValue,
        string description,
        bool includeConfigPlaceholders)
    {
        if (string.IsNullOrWhiteSpace(actualValue))
        {
            return AddRequirement(requirements, referencePrefix, settingPath, "Config", description, appliedToDraft: true);
        }

        _ = AddRequirement(requirements, referencePrefix, settingPath, "Config", description, appliedToDraft: false, actualValue: actualValue);
        return includeConfigPlaceholders ? actualValue : actualValue;
    }

    private static string? TryGetTransportProperty(IReadOnlyDictionary<string, string> properties, string name)
        => properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    private static string ExtractHost(string address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    private static bool LooksLikeFilePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return uri.IsFile || string.Equals(uri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);
        }

        return value.Contains('\\', StringComparison.Ordinal)
            || Regex.IsMatch(value, "^[A-Za-z]:[\\/]");
    }

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

    private static string ResolveContentType(string renderModule)
    {
        return renderModule switch
        {
            "hl7-render" => "text/hl7-v2",
            "flat-file-render" => "text/plain",
            "json-render" => "application/json",
            _ => "application/xml"
        };
    }

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

    private static BizTalkProjectResponse CreateDefaultProjectProfile()
        => new("ImportedBinding", string.Empty, 0, 0, 1, 0, [], []);

    private sealed record AnalysisScope(
        string SourcePath,
        string SourceType,
        string DisplayName,
        string RootDirectory,
        IReadOnlyList<string> BizTalkProjects,
        IReadOnlyList<string> CustomProjects);

    private sealed record SolutionProjectSet(
        IReadOnlyList<string> BizTalkProjects,
        IReadOnlyList<string> CustomProjects);

    private sealed record BizTalkProjectAnalysis(BizTalkProjectResponse Response, IReadOnlyList<string> References);

    private sealed record BindingFileAnalysis(
        string Name,
        string FilePath,
        IReadOnlyList<ReceivePortAnalysis> ReceivePorts,
        IReadOnlyList<SendPortAnalysis> SendPorts);

    private sealed record ReceivePortAnalysis(
        string Name,
        IReadOnlyList<ReceiveLocationAnalysis> ReceiveLocations);

    private sealed record ReceiveLocationAnalysis(
        string Name,
        string TransportType,
        string Address,
        string ReceivePipeline,
        IReadOnlyDictionary<string, string> TransportProperties);

    private sealed record SendPortAnalysis(
        string Name,
        string Description,
        string TransportType,
        string Address,
        string TransmitPipeline,
        string ReceivePipeline,
        string FilterExpression,
        IReadOnlyDictionary<string, string> TransportProperties);
}
