using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService : IBizTalkImportService
{
    private static readonly Regex SolutionProjectPattern = new("Project\\(.*\\)\\s=\\s\"[^\"]+\",\\s\"(?<path>[^\"]+)\"", RegexOptions.Compiled);
    private static readonly Regex MultipleDashesPattern = new("-+", RegexOptions.Compiled);

    /// <summary>
    /// Maps the MSBuild item element names used by BizTalk project (.btproj) files to the
    /// artifact kind Mulse reports. This table is the single place to teach the importer about
    /// a new BizTalk artifact type (for example Business Rules ".rules" policies), so contributors
    /// can extend BizTalk import support without touching the analysis logic itself.
    /// </summary>
    private static readonly IReadOnlyList<(string ItemName, string ArtifactKind)> ArtifactItemMappings =
    [
        ("XLang", "Orchestration"),
        ("Map", "Map"),
        ("Schema", "Schema"),
        ("Pipeline", "Pipeline"),
    ];

    public BizTalkImportService(IEnumerable<IBizTalkAssemblyKindClassifier>? assemblyKindClassifiers = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // Custom classifiers (typically contributed via DI by an organization or OSS module) get first
        // opportunity to classify a custom assembly; the built-in keyword classifier is always appended as
        // the final fallback so behavior remains sensible with no registrations at all.
        _assemblyKindClassifiers = [.. assemblyKindClassifiers ?? [], new KeywordBizTalkAssemblyKindClassifier()];
    }

    private readonly IReadOnlyList<IBizTalkAssemblyKindClassifier> _assemblyKindClassifiers;

    public Task<BizTalkSolutionAnalysisResponse> AnalyzeAsync(AnalyzeBizTalkSolutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var resolvedPath = Path.GetFullPath(request.SourcePath);

        var warnings = new List<string>
        {
            "Generated draft flows are imported disabled so placeholder configuration and secrets can be reviewed before runtime use.",
            "BizTalk orchestration Decision shapes are auto-translated into declarative decision-augment rules where the branch expression is simple enough to translate confidently; convoys, correlation sets, atomic transactions, loops, and anything else too complex are scaffolded as starter C# modules instead."
        };

        var scope = ResolveScope(resolvedPath);
        var scopes = new List<AnalysisScope> { scope };
        foreach (var additionalSourcePath in request.AdditionalSourcePaths)
        {
            if (string.IsNullOrWhiteSpace(additionalSourcePath))
            {
                continue;
            }

            var additionalResolvedPath = Path.GetFullPath(additionalSourcePath);
            if (scopes.Any(existing => string.Equals(existing.SourcePath, additionalResolvedPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            try
            {
                scopes.Add(ResolveScope(additionalResolvedPath));
            }
            catch (ArgumentException exception)
            {
                warnings.Add($"Additional source path '{additionalSourcePath}' was skipped: {exception.Message}");
            }
        }

        // Each scope keeps its own root directory: artifact relative paths stay meaningful per source, and a
        // referenced project living in a different solution still resolves to a real path on disk.
        var bizTalkProjectPaths = new List<(string ProjectPath, string RootDirectory)>();
        var customProjectPaths = new List<(string ProjectPath, string RootDirectory)>();
        var seenProjectPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var analysisScope in scopes)
        {
            foreach (var projectPath in analysisScope.BizTalkProjects.Where(projectPath => seenProjectPaths.Add(projectPath)))
            {
                bizTalkProjectPaths.Add((projectPath, analysisScope.RootDirectory));
            }

            foreach (var projectPath in analysisScope.CustomProjects.Where(projectPath => seenProjectPaths.Add(projectPath)))
            {
                customProjectPaths.Add((projectPath, analysisScope.RootDirectory));
            }
        }

        if (bizTalkProjectPaths.Count == 0)
        {
            throw new InvalidOperationException($"The source '{resolvedPath}' does not contain any BizTalk project files.");
        }

        if (scopes.Count > 1)
        {
            warnings.Add($"{scopes.Count} source paths were analyzed as one combined migration workspace, so cross-solution project references (shared schema, map, and pipeline libraries) resolve instead of falling back to generic guesses.");
        }

        var bizTalkProjects = new List<BizTalkProjectAnalysis>(bizTalkProjectPaths.Count);
        foreach (var (projectPath, projectRootDirectory) in bizTalkProjectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bizTalkProjects.Add(AnalyzeBizTalkProject(projectPath, projectRootDirectory));
            }
            catch (XmlException exception)
            {
                warnings.Add($"Unable to parse BizTalk project '{projectPath}': {exception.Message}");
            }
        }

        var customAssemblies = new List<BizTalkCustomAssemblyResponse>(customProjectPaths.Count);
        foreach (var (projectPath, projectRootDirectory) in customProjectPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                customAssemblies.Add(AnalyzeCustomProject(projectPath, projectRootDirectory, bizTalkProjects, _assemblyKindClassifiers));
            }
            catch (XmlException exception)
            {
                warnings.Add($"Unable to parse custom project '{projectPath}': {exception.Message}");
            }
        }

        var bindingAnalyses = new List<BindingFileAnalysis>();
        var bindingFilePaths = scopes
            .Select(static analysisScope => analysisScope.RootDirectory)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(DiscoverBindingFiles)
            .Concat(ResolveAdditionalBindingFiles(request.AdditionalBindingPaths, warnings))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase);
        foreach (var bindingPath in bindingFilePaths)
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
            warnings.Add("No BizTalk binding files were discovered, so fetch and deliver drafts fall back to generic repository-style modules and placeholders. BizTalk bindings are often deployed separately from source control (for example via the BizTalk Administration Console's 'Export Bindings', 'BTSTask ExportBindings', or a deployment framework's environment settings). Export them and pass their location(s) via AdditionalBindingPaths to improve the generated drafts.");
        }

        var projectsByName = bizTalkProjects.ToDictionary(static project => project.Response.Name, StringComparer.OrdinalIgnoreCase);
        var hydratedAnalyses = bizTalkProjects
            .Select(project => project with
            {
                Response = project.Response with
                {
                    ReferencedCustomAssemblies = customAssemblies
                        .Where(assembly => assembly.UsedByProjects.Contains(project.Response.Name, StringComparer.OrdinalIgnoreCase))
                        .Select(assembly => assembly.Name)
                        .ToArray(),
                    ReferencedArtifacts = ResolveReferencedArtifacts(project, projectsByName)
                }
            })
            .ToArray();
        var hydratedProjects = hydratedAnalyses.Select(static project => project.Response).ToArray();

        var candidateProjects = hydratedAnalyses
            .Where(project => ShouldCreateFlowCandidate(project.Response, bindingAnalyses))
            .ToArray();

        if (candidateProjects.Length < hydratedAnalyses.Length)
        {
            warnings.Add($"{hydratedAnalyses.Length - candidateProjects.Length} BizTalk support project(s) were inventoried but not turned into standalone draft flows because they look like shared schema libraries without direct bindings, maps, pipelines, or orchestrations.");
        }

        if (candidateProjects.Length == 0)
        {
            warnings.Add("No draft flows were generated: every analyzed project looks like a shared schema library (no orchestrations, maps, pipelines, or matching binding file). Point the importer at the project or solution that owns the orchestration/pipeline, or supply its binding export via AdditionalBindingPaths.");
        }

        var flowCandidates = candidateProjects
            .SelectMany(project => CreateFlowCandidates(project, customAssemblies, bindingAnalyses))
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

        foreach (var (itemName, artifactKind) in ArtifactItemMappings)
        {
            AddArtifacts(document, projectDirectory, rootDirectory, itemName, artifactKind, artifacts);
        }

        var references = document.Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "Reference", StringComparison.Ordinal)
                || string.Equals(element.Name.LocalName, "ProjectReference", StringComparison.Ordinal))
            .Select(static reference => NormalizeReferenceName(
                reference.Attribute("Include")?.Value,
                reference.Elements().FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Name", StringComparison.Ordinal))?.Value))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var orchestrationSignals = DetectOrchestrationControlFlowSignalsByOrchestration(artifacts);
        var (receivePipelineCount, sendPipelineCount) = ClassifyPipelineDirections(artifacts);

        return new BizTalkProjectAnalysis(
            new BizTalkProjectResponse(
                projectName,
                projectPath,
                artifacts.Count(static artifact => string.Equals(artifact.Kind, "Orchestration", StringComparison.OrdinalIgnoreCase)),
                artifacts.Count(static artifact => string.Equals(artifact.Kind, "Map", StringComparison.OrdinalIgnoreCase)),
                artifacts.Count(static artifact => string.Equals(artifact.Kind, "Schema", StringComparison.OrdinalIgnoreCase)),
                artifacts.Count(static artifact => string.Equals(artifact.Kind, "Pipeline", StringComparison.OrdinalIgnoreCase)),
                [],
                artifacts)
            {
                OrchestrationControlFlowSignals = AggregateOrchestrationControlFlowSignals(orchestrationSignals),
                OrchestrationSignalsByOrchestration = orchestrationSignals,
                OrchestrationDecisionAnalyses = AnalyzeOrchestrationDecisions(artifacts, projectName),
                MapAnalyses = AnalyzeMaps(artifacts),
                ReceivePipelineCount = receivePipelineCount,
                SendPipelineCount = sendPipelineCount
            },
            references);
    }

    /// <summary>
    /// BizTalk orchestration designer shape types that signal control-flow complexity the importer doesn't
    /// attempt to auto-translate (see <see cref="OrchestrationControlFlowSignalResponse"/>). Detected via a
    /// lightweight text scan of .odx designer metadata rather than full ODX parsing, since .odx files mix XML
    /// designer metadata with generated C# and aren't valid, well-formed XML on their own. Extend this table
    /// to teach the importer about additional shape types worth calling out.
    /// </summary>
    private static readonly IReadOnlyList<(string ShapeType, string Description)> OrchestrationControlFlowShapes =
    [
        ("Listen", "Listen shape(s) (convoy / racing receive branches)"),
        ("Parallel", "Parallel action shape(s)"),
        ("CorrelationDeclaration", "correlation set usage(s)"),
        ("CorrelationType", "correlation type declaration(s)"),
        ("AtomicTransaction", "atomic transaction scope(s)"),
        ("Decision", "Decision/branch shape(s)"),
        ("Loop", "Loop shape(s)"),
    ];

    /// <summary>
    /// Detects control-flow shape signals for each orchestration artifact individually (rather than only as a
    /// project-wide total), so a project containing several orchestrations can be split into one draft flow per
    /// orchestration with correctly scoped complexity reporting. See
    /// <see cref="AggregateOrchestrationControlFlowSignals"/> for the project-wide roll-up.
    /// </summary>
    private static IReadOnlyList<BizTalkOrchestrationSignalsResponse> DetectOrchestrationControlFlowSignalsByOrchestration(
        IReadOnlyList<BizTalkArtifactResponse> artifacts)
    {
        var results = new List<BizTalkOrchestrationSignalsResponse>();
        foreach (var artifact in OrchestrationArtifacts(artifacts))
        {
            string content;
            try
            {
                content = File.ReadAllText(artifact.FullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            var signals = new List<OrchestrationControlFlowSignalResponse>();
            foreach (var (shapeType, description) in OrchestrationControlFlowShapes)
            {
                var matchCount = Regex.Matches(content, $"Type=\"{Regex.Escape(shapeType)}\"", RegexOptions.None).Count;
                if (matchCount > 0)
                {
                    signals.Add(new OrchestrationControlFlowSignalResponse(shapeType, description, matchCount));
                }
            }

            results.Add(new BizTalkOrchestrationSignalsResponse(OrchestrationName(artifact), signals));
        }

        return results;
    }

    /// <summary>Sums per-orchestration control-flow signals into the project-wide totals.</summary>
    private static IReadOnlyList<OrchestrationControlFlowSignalResponse> AggregateOrchestrationControlFlowSignals(
        IReadOnlyList<BizTalkOrchestrationSignalsResponse> orchestrationSignals)
    {
        var counts = orchestrationSignals
            .SelectMany(static orchestration => orchestration.Signals)
            .GroupBy(static signal => signal.ShapeType, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.Sum(static signal => signal.Count), StringComparer.Ordinal);

        return OrchestrationControlFlowShapes
            .Where(shape => counts.ContainsKey(shape.ShapeType))
            .Select(shape => new OrchestrationControlFlowSignalResponse(shape.ShapeType, shape.Description, counts[shape.ShapeType]))
            .ToArray();
    }

    /// <summary>
    /// Runs <see cref="BizTalkOrchestrationDecisionAnalyzer"/> over every orchestration artifact, translating
    /// simple BizTalk Decision branches into real <c>decision-augment</c> rules and scaffolding a starter C#
    /// module for anything too complex. Never throws: the analyzer degrades to an empty result per file, and
    /// orchestrations with nothing to report are dropped so the response stays signal-only.
    /// </summary>
    private static IReadOnlyList<BizTalkOrchestrationDecisionAnalysisResponse> AnalyzeOrchestrationDecisions(
        IReadOnlyList<BizTalkArtifactResponse> artifacts,
        string projectName)
    {
        var namespaceHint = $"Mulse.Migrated.{CreatePascalCaseNamespaceSegment(projectName)}";
        return OrchestrationArtifacts(artifacts)
            .Select(artifact => BizTalkOrchestrationDecisionAnalyzer.Analyze(artifact.FullPath, OrchestrationName(artifact), namespaceHint))
            .Where(static analysis => analysis.GeneratedDecisionJson is not null
                || analysis.ComplexBranches.Count > 0
                || analysis.ComplexControlFlowShapeCounts.Count > 0)
            .ToArray();
    }

    /// <summary>
    /// Runs <see cref="BizTalkMapAnalyzer"/> over every map (.btm) artifact, producing a best-effort XSLT 1.0
    /// translation of the map's direct field links and constant assignments. Maps that can't be parsed are
    /// skipped (the analyzer returns null) so an unreadable map never fails the whole analysis.
    /// </summary>
    private static IReadOnlyList<BizTalkMapAnalysisResponse> AnalyzeMaps(IReadOnlyList<BizTalkArtifactResponse> artifacts)
    {
        return artifacts
            .Where(static artifact => string.Equals(artifact.Kind, "Map", StringComparison.OrdinalIgnoreCase))
            .Select(artifact => BizTalkMapAnalyzer.Analyze(artifact.FullPath, Path.GetFileNameWithoutExtension(artifact.Name)))
            .Where(static analysis => analysis is not null)
            .Select(static analysis => analysis!)
            .ToArray();
    }

    /// <summary>
    /// Well-known BizTalk pipeline stage category GUIDs that only appear in receive pipelines
    /// (Decode, Disassemble, Validate, Resolve Party).
    /// </summary>
    private static readonly IReadOnlyList<string> ReceivePipelineStageCategoryIds =
    [
        "9d0e4103-4cce-4536-83fa-4a5040674ad6",
        "9d0e4105-4cce-4536-83fa-4a5040674ad6",
        "9d0e410d-4cce-4536-83fa-4a5040674ad6",
        "9d0e410e-4cce-4536-83fa-4a5040674ad6",
    ];

    /// <summary>
    /// Well-known BizTalk pipeline stage category GUIDs that only appear in send pipelines
    /// (Pre-Assemble, Assemble, Encode).
    /// </summary>
    private static readonly IReadOnlyList<string> SendPipelineStageCategoryIds =
    [
        "9d0e4101-4cce-4536-83fa-4a5040674ad6",
        "9d0e4107-4cce-4536-83fa-4a5040674ad6",
        "9d0e4108-4cce-4536-83fa-4a5040674ad6",
    ];

    /// <summary>
    /// Classifies each pipeline (.btp) artifact as receive- or send-direction by inspecting the stage category
    /// GUIDs it declares, so a project that only receives messages isn't given a bogus bidirectional draft. A
    /// pipeline's file name is a hint at best ("XReceivePipeline"), but its stage categories are authoritative:
    /// BizTalk only allows Decode/Disassemble/Validate/ResolveParty stages in receive pipelines and
    /// Pre-Assemble/Assemble/Encode stages in send pipelines.
    /// </summary>
    private static (int ReceiveCount, int SendCount) ClassifyPipelineDirections(IReadOnlyList<BizTalkArtifactResponse> artifacts)
    {
        var receiveCount = 0;
        var sendCount = 0;

        foreach (var artifact in artifacts.Where(static artifact => string.Equals(artifact.Kind, "Pipeline", StringComparison.OrdinalIgnoreCase)))
        {
            string content;
            try
            {
                content = File.ReadAllText(artifact.FullPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (SendPipelineStageCategoryIds.Any(categoryId => content.Contains(categoryId, StringComparison.OrdinalIgnoreCase)))
            {
                sendCount++;
            }
            else if (ReceivePipelineStageCategoryIds.Any(categoryId => content.Contains(categoryId, StringComparison.OrdinalIgnoreCase)))
            {
                receiveCount++;
            }
        }

        return (receiveCount, sendCount);
    }

    private static IEnumerable<BizTalkArtifactResponse> OrchestrationArtifacts(IReadOnlyList<BizTalkArtifactResponse> artifacts)
        => artifacts.Where(static artifact => string.Equals(artifact.Kind, "Orchestration", StringComparison.OrdinalIgnoreCase));

    private static string OrchestrationName(BizTalkArtifactResponse artifact)
        => Path.GetFileNameWithoutExtension(artifact.Name);

    /// <summary>
    /// Name fragments that mark an orchestration (or project) as a test, debug, or scratch artifact rather
    /// than a production process. Real BizTalk solutions routinely keep such orchestrations checked in next to
    /// production ones, so flagging them lets a reviewer deprioritize their migration.
    /// </summary>
    private static readonly IReadOnlyList<string> TestArtifactNameFragments =
    [
        "test", "debug", "scratch", "poc", "temp", "sandbox", "sample", "obsolete", "deprecated", "backup"
    ];

    /// <summary>
    /// Returns true when <paramref name="name"/> looks like a test/debug/scratch artifact. Matching is done on
    /// word-ish boundaries (PascalCase segments, separators) rather than raw substring containment so genuine
    /// production names such as "Consumption", "Attempt", or "Protocol" aren't flagged by accident.
    /// </summary>
    private static bool IsLikelyTestArtifactName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var segments = Regex.Split(name, "(?<=[a-z0-9])(?=[A-Z])|[^A-Za-z0-9]+")
            .Where(static segment => segment.Length > 0)
            .Select(static segment => segment.ToLowerInvariant())
            .ToArray();

        return segments.Any(segment => TestArtifactNameFragments.Contains(segment, StringComparer.Ordinal));
    }

    /// <summary>
    /// Turns a BizTalk project name into a single PascalCase namespace segment for generated module source
    /// (e.g. "Shs.SpaceTrax2.Interfaces.Core.Process.Billing" becomes "ShsSpaceTrax2InterfacesCoreProcessBilling").
    /// </summary>
    private static string CreatePascalCaseNamespaceSegment(string projectName)
    {
        var slug = CreateSlug(projectName);
        var segment = string.Concat(slug
            .Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (string.IsNullOrWhiteSpace(segment))
        {
            return "Imported";
        }

        return char.IsDigit(segment[0]) ? $"Project{segment}" : segment;
    }
}
