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

    /// <summary>
    /// Resolves the set of artifacts owned by BizTalk projects that <paramref name="project"/> depends on
    /// (directly or transitively), such as shared schema or pipeline library projects. Many orchestration
    /// projects don't ship their own schemas or pipelines but instead reference a separate library project,
    /// so classification heuristics (parse/render module selection) need this signal in addition to the
    /// project's own <see cref="BizTalkProjectResponse.Artifacts"/> to avoid falling back to generic guesses.
    /// </summary>
    private static IReadOnlyList<BizTalkArtifactResponse> ResolveReferencedArtifacts(
        BizTalkProjectAnalysis project,
        IReadOnlyDictionary<string, BizTalkProjectAnalysis> projectsByName)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { project.Response.Name };
        var collected = new List<BizTalkArtifactResponse>();
        var pending = new Queue<string>(project.References);

        while (pending.Count > 0)
        {
            var referenceName = pending.Dequeue();
            if (!visited.Add(referenceName) || !projectsByName.TryGetValue(referenceName, out var referencedProject))
            {
                continue;
            }

            collected.AddRange(referencedProject.Response.Artifacts);
            foreach (var transitiveReference in referencedProject.References)
            {
                pending.Enqueue(transitiveReference);
            }
        }

        return collected;
    }

    /// <summary>
    /// Combines a project's own artifacts with artifacts inherited from referenced BizTalk library projects,
    /// for use by heuristics that decide how to shape a draft flow (e.g. parse/render module selection).
    /// </summary>
    private static IEnumerable<BizTalkArtifactResponse> AllArtifacts(BizTalkProjectResponse project)
        => project.Artifacts.Concat(project.ReferencedArtifacts);

    private static BizTalkCustomAssemblyResponse AnalyzeCustomProject(
        string projectPath,
        string rootDirectory,
        IReadOnlyList<BizTalkProjectAnalysis> bizTalkProjects,
        IReadOnlyList<IBizTalkAssemblyKindClassifier> assemblyKindClassifiers)
    {
        var document = XDocument.Load(projectPath, LoadOptions.None);
        var assemblyName = GetProperty(document, "AssemblyName") ?? Path.GetFileNameWithoutExtension(projectPath);
        var usedByProjects = bizTalkProjects
            .Where(project => project.References.Contains(assemblyName, StringComparer.OrdinalIgnoreCase)
                || project.References.Contains(Path.GetFileNameWithoutExtension(projectPath), StringComparer.OrdinalIgnoreCase))
            .Select(project => project.Response.Name)
            .ToArray();
        var projectDirectory = Path.GetDirectoryName(projectPath) ?? rootDirectory;
        var sourceFileNames = document.Descendants()
            .Where(static element => string.Equals(element.Name.LocalName, "Compile", StringComparison.Ordinal))
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value) && value!.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            .Select(static value => Path.GetFileName(value!))
            .ToArray();

        if (sourceFileNames.Length == 0 && Directory.Exists(projectDirectory))
        {
            sourceFileNames = Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
                .Select(static path => Path.GetFileName(path))
                .ToArray();
        }

        var classificationContext = new BizTalkAssemblyClassificationContext(assemblyName, projectPath, sourceFileNames);
        var suggestedModuleKind = ClassifyAssembly(assemblyKindClassifiers, classificationContext);
        var migrationApproach = CreateMigrationApproach(suggestedModuleKind, assemblyName);

        return new BizTalkCustomAssemblyResponse(
            assemblyName,
            projectPath,
            sourceFileNames.Length,
            suggestedModuleKind,
            migrationApproach,
            false,
            usedByProjects);
    }

    /// <summary>
    /// Runs the registered <see cref="IBizTalkAssemblyKindClassifier"/> pipeline for a custom BizTalk assembly,
    /// returning the first non-null suggestion. Falls back to "OrchestrationAugment" if every classifier
    /// (including the built-in keyword fallback) declines to classify the assembly.
    /// </summary>
    private static string ClassifyAssembly(IReadOnlyList<IBizTalkAssemblyKindClassifier> classifiers, BizTalkAssemblyClassificationContext context)
    {
        foreach (var classifier in classifiers)
        {
            var moduleKind = classifier.TryClassify(context);
            if (!string.IsNullOrWhiteSpace(moduleKind))
            {
                return moduleKind;
            }
        }

        return "OrchestrationAugment";
    }

    /// <summary>
    /// Produces the draft flow candidate(s) for one BizTalk project. A project containing more than one
    /// orchestration is split into one candidate per orchestration (each scoped to just that orchestration's
    /// control-flow signals, decision rules, and scaffolded modules) instead of collapsing every
    /// orchestration's logic into a single flow, which produced unusable merged rule sets for real projects.
    /// </summary>
    private static IReadOnlyList<BizTalkFlowCandidateResponse> CreateFlowCandidates(
        BizTalkProjectAnalysis project,
        IReadOnlyList<BizTalkCustomAssemblyResponse> customAssemblies,
        IReadOnlyList<BindingFileAnalysis> bindingFiles)
    {
        var orchestrationNames = OrchestrationArtifacts(project.Response.Artifacts)
            .Select(OrchestrationName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (orchestrationNames.Length <= 1)
        {
            return [CreateFlowCandidate(project, customAssemblies, bindingFiles, split: null)];
        }

        return orchestrationNames
            .Select(orchestrationName => CreateFlowCandidate(
                project,
                customAssemblies,
                bindingFiles,
                new OrchestrationSplit(orchestrationName, orchestrationNames.Length)))
            .ToArray();
    }

    private static BizTalkFlowCandidateResponse CreateFlowCandidate(
        BizTalkProjectAnalysis project,
        IReadOnlyList<BizTalkCustomAssemblyResponse> customAssemblies,
        IReadOnlyList<BindingFileAnalysis> bindingFiles,
        OrchestrationSplit? split)
    {
        var effectiveProject = ScopeProjectToOrchestration(project.Response, split);
        var flowId = split is null
            ? CreateFlowId(project.Response.Name)
            : CreateFlowId($"{project.Response.Name}-{split.OrchestrationName}");
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

        if (effectiveProject.MapCount > 0)
        {
            augmentGuidance.Add(effectiveProject.MapCount == 1
                ? "Convert the BizTalk map into XSLT or an equivalent render step before enabling the draft flow."
                : $"Convert the {effectiveProject.MapCount} BizTalk maps into XSLT or explicit render steps and review which one belongs on each delivery route.");
        }

        var translatedMaps = effectiveProject.MapAnalyses
            .Where(static analysis => analysis.GeneratedXsltSourceCode is not null)
            .ToArray();
        if (translatedMaps.Length > 0)
        {
            augmentGuidance.Add($"{translatedMaps.Length} BizTalk map(s) were auto-translated into starter XSLT stylesheets ({string.Join(", ", translatedMaps.Select(static map => map.ScaffoldedModuleFileName))}). Download them with the scaffolded-module endpoints, review the generated XPath, then point the render step's xsltPath at the saved file.");
        }

        var untranslatedMapLinks = effectiveProject.MapAnalyses.Sum(static analysis => analysis.FunctoidInvolvedLinkCount);
        if (untranslatedMapLinks > 0)
        {
            augmentGuidance.Add($"{untranslatedMapLinks} BizTalk map link(s) route through functoids (transformation logic) rather than a direct field-to-field assignment and were NOT translated. Implement them by hand in the generated XSLT, or replace the render step with a custom module.");
        }

        var deliveryGuidance = new List<string>
        {
            relatedBindings.Length > 0
                ? "The draft deliveries were pre-populated from BizTalk send ports and still need transport-specific validation."
                : "No binding-backed send ports were found, so the draft uses a repository-style delivery placeholder."
        };

        var openQuestions = new List<string>();
        if (effectiveProject.OrchestrationCount > 0)
        {
            openQuestions.Add(effectiveProject.OrchestrationControlFlowSignals.Count > 0
                ? $"Which orchestration branches, subscriptions, and correlations should become separate Mulse flows versus augment steps? Detected {string.Join(", ", effectiveProject.OrchestrationControlFlowSignals.Select(static signal => $"{signal.Count} {signal.Description}"))} that were used to scaffold decision/stateful-orchestration augment steps below - review the generated rules and correlation values before enabling the flow."
                : "Which orchestration branches, subscriptions, and correlations should become separate Mulse flows versus augment steps?");
        }

        if (effectiveProject.PipelineCount > 0)
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
        else if (relatedBindings.Any(static binding => binding.SendPorts.Any(static sendPort => sendPort.IsTwoWay) || binding.ReceivePorts.Any(static receivePort => receivePort.IsTwoWay)))
        {
            openQuestions.Add("This project uses two-way (solicit-response) WCF ports. Mulse's fetch/deliver module contracts are currently one-way - decide whether to add a response-capturing module, model the reply as a separate inbound flow, or keep this route in a temporary wrapper during cutover.");
        }

        var requirements = new List<BizTalkSettingRequirementResponse>();
        var draftWarnings = new List<string>
        {
            "This draft is disabled by default so imported addresses, credentials, and map paths can be reviewed safely before execution."
        };

        if (split is not null)
        {
            draftWarnings.Add($"The source project contains {split.TotalOrchestrations} orchestrations, so it was split into one draft flow per orchestration. This draft covers '{split.OrchestrationName}' only; the others are separate candidates that share the same bindings and artifacts.");
        }

        var fetchStep = CreateFetchStep(flowId, relatedBindings, requirements, draftWarnings);
        var parseStep = CreateParseStep(flowId, effectiveProject, requirements, draftWarnings);
        var deliveries = CreateDeliveryRoutes(flowId, effectiveProject, relatedBindings, requirements, draftWarnings);
        var augments = CreateAugments(flowId, effectiveProject, relatedAssemblies, deliveries.Count, requirements, draftWarnings);

        var isPushTriggeredFetch = string.Equals(fetchStep.Module, "http-inbound-fetch", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fetchStep.Module, "file-system-watcher-fetch", StringComparison.OrdinalIgnoreCase);
        if (string.Equals(fetchStep.Module, "http-inbound-fetch", StringComparison.OrdinalIgnoreCase))
        {
            draftWarnings.Add($"This fetch step listens for pushed HTTP requests at /api/inbound/{{route}} instead of polling. The original BizTalk WCF/HTTP endpoint consumers must be repointed at this URL, and the flow's trigger mode must be set to 'Push' before it will register.");
        }

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
            "xml-envelope-debatch-parse" => "The draft debatches an XML envelope because the imported receive pipeline configures an envelope schema (EnvelopeSpecNames).",
            "hl7-parse" => "The draft uses HL7 parsing because the project name or assets look HL7-specific.",
            "flat-file-parse" => "The draft uses flat-file parsing because the project appears to process delimited or flat-file content.",
            "xml-xsd-parse" => "The draft uses XML/XSD-aware parsing and preloads discovered schema paths where available.",
            _ => "The draft uses JSON parsing as a fallback and should be replaced if the inbound message shape is not JSON."
        };

        if (effectiveProject.Artifacts.Count == 0 && effectiveProject.ReferencedArtifacts.Count > 0)
        {
            parseGuidance += " This project doesn't ship its own schemas or pipelines, so the signal was inferred from a referenced BizTalk library project; confirm the correct schema/pipeline library is in scope.";
        }

        AddPipelineDirectionGuidance(effectiveProject, relatedBindings, deliveryGuidance, openQuestions, draftWarnings);

        var scaffoldedModules = CollectScaffoldedModules(effectiveProject);
        if (scaffoldedModules.Count > 0)
        {
            openQuestions.Add($"{scaffoldedModules.Count} starter artifact(s) were scaffolded for logic that could not be auto-translated ({string.Join(", ", scaffoldedModules.Select(static module => module.FileName))}). Each one compiles or parses as-is; who owns implementing and registering them before this flow is enabled?");
        }

        var isLikelyTestArtifact = split is not null
            ? IsLikelyTestArtifactName(split.OrchestrationName)
            : IsLikelyTestArtifactName(project.Response.Name)
                || OrchestrationArtifacts(project.Response.Artifacts).Any(artifact => IsLikelyTestArtifactName(OrchestrationName(artifact)));
        if (isLikelyTestArtifact)
        {
            draftWarnings.Add("The source orchestration or project name looks like a test, debug, or scratch artifact rather than a production process. Confirm it is worth migrating before spending review effort on this draft.");
        }

        var draftFlow = new BizTalkDraftFlowResponse(
            flowId,
            false,
            new BizTalkDraftTriggerResponse(isPushTriggeredFetch ? PipelineTriggerMode.Push : PipelineTriggerMode.OnDemand, null, false),
            [new BizTalkDraftSourceResponse(PrimarySourceId, fetchStep, parseStep, [])],
            augments,
            deliveries,
            requirements,
            draftWarnings);

        var summary = $"{effectiveProject.OrchestrationCount} orchestration(s), {effectiveProject.MapCount} map(s), {effectiveProject.SchemaCount} schema(s), {effectiveProject.PipelineCount} pipeline(s), {relatedBindings.Sum(static binding => binding.SendPorts.Count)} send port(s).";
        if (split is not null)
        {
            summary = $"Orchestration '{split.OrchestrationName}' (1 of {split.TotalOrchestrations} in the project): {summary}";
        }

        return new BizTalkFlowCandidateResponse(
            flowId,
            project.Response.Name,
            summary,
            relatedBindings.Length > 0
                ? "Receive and send bindings were imported into the draft flow. Review the generated transport settings and trigger strategy."
                : "No receive binding was found for this project, so the draft fetch step uses a repository-style staging inbox placeholder.",
            parseGuidance,
            augmentGuidance,
            deliveryGuidance,
            openQuestions,
            project.Response.Artifacts.Select(static artifact => artifact.Name).ToArray(),
            relatedBindings.Select(static binding => binding.Name).ToArray(),
            draftFlow)
        {
            ScaffoldedModules = scaffoldedModules,
            IsLikelyTestArtifact = isLikelyTestArtifact
        };
    }

    /// <summary>
    /// The single generated source id used by BizTalk-derived drafts. A BizTalk receive port maps to exactly
    /// one inbound transport, so the generated source graph has one root source; additional sources (lookups,
    /// joins) are something a reviewer adds deliberately in the designer after import.
    /// </summary>
    private const string PrimarySourceId = "primary";

    /// <summary>
    /// Narrows a project response to a single orchestration's view of itself (orchestration count, control-flow
    /// signals, and decision analyses), so a split candidate reports and scaffolds only what belongs to its own
    /// orchestration. Returns the project unchanged when it is not being split.
    /// </summary>
    private static BizTalkProjectResponse ScopeProjectToOrchestration(BizTalkProjectResponse project, OrchestrationSplit? split)
    {
        if (split is null)
        {
            return project;
        }

        var orchestrationSignals = project.OrchestrationSignalsByOrchestration
            .Where(signals => string.Equals(signals.OrchestrationName, split.OrchestrationName, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return project with
        {
            OrchestrationCount = 1,
            OrchestrationSignalsByOrchestration = orchestrationSignals,
            OrchestrationControlFlowSignals = AggregateOrchestrationControlFlowSignals(orchestrationSignals),
            OrchestrationDecisionAnalyses = project.OrchestrationDecisionAnalyses
                .Where(analysis => string.Equals(analysis.OrchestrationName, split.OrchestrationName, StringComparison.OrdinalIgnoreCase))
                .ToArray()
        };
    }

    /// <summary>
    /// Collects every starter artifact generated for a candidate: C# module skeletons for orchestrations whose
    /// control flow could not be auto-translated, plus XSLT stylesheets generated from BizTalk maps. Both reuse
    /// <see cref="BizTalkScaffoldedModuleResponse"/> (and therefore the existing zip/single-file download
    /// endpoints) because that contract is content-agnostic.
    /// </summary>
    private static IReadOnlyList<BizTalkScaffoldedModuleResponse> CollectScaffoldedModules(BizTalkProjectResponse project)
    {
        var modules = project.OrchestrationDecisionAnalyses
            .Where(static analysis => analysis.ScaffoldedModuleSourceCode is not null)
            .Select(static analysis => new BizTalkScaffoldedModuleResponse(
                analysis.ScaffoldedModuleFileName ?? $"{analysis.OrchestrationName}.cs",
                analysis.ScaffoldedModuleId ?? analysis.OrchestrationName,
                analysis.OrchestrationName,
                analysis.ScaffoldedModuleSourceCode!))
            .ToList();

        modules.AddRange(project.MapAnalyses
            .Where(static analysis => analysis.GeneratedXsltSourceCode is not null)
            .Select(static analysis => new BizTalkScaffoldedModuleResponse(
                analysis.ScaffoldedModuleFileName ?? $"{analysis.MapName}.xslt",
                analysis.ScaffoldedModuleId ?? analysis.MapName,
                analysis.MapName,
                analysis.GeneratedXsltSourceCode!)));

        return modules;
    }

    /// <summary>
    /// Adds direction-aware guidance when a project's pipelines say it only ever receives (or only ever sends)
    /// messages. Without this, an inbound-only interface still gets a full bidirectional draft whose delivery
    /// route is pure placeholder noise, with nothing telling the reviewer that is expected.
    /// </summary>
    private static void AddPipelineDirectionGuidance(
        BizTalkProjectResponse project,
        IReadOnlyList<BindingFileAnalysis> relatedBindings,
        List<string> deliveryGuidance,
        List<string> openQuestions,
        List<string> draftWarnings)
    {
        if (project.PipelineCount == 0 || project.OrchestrationCount > 0)
        {
            return;
        }

        var hasSendBindings = relatedBindings.Any(static binding => binding.SendPorts.Count > 0);
        if (project.ReceivePipelineCount > 0 && project.SendPipelineCount == 0 && !hasSendBindings)
        {
            deliveryGuidance.Add($"This project only contains receive-direction pipeline(s) ({project.ReceivePipelineCount}) and has no send pipeline or send-port binding, so it looks like an inbound-only interface. The generated delivery route is a placeholder that probably does not correspond to anything in the source system.");
            openQuestions.Add("This inbound-only BizTalk project has no outbound counterpart. Where should the received messages actually be delivered in Mulse, or should this draft become the fetch/parse half of an existing flow instead of a standalone one?");
            draftWarnings.Add("Inbound-only project detected from pipeline stage categories: review or remove the placeholder delivery route before enabling this flow.");
        }
        else if (project.SendPipelineCount > 0 && project.ReceivePipelineCount == 0)
        {
            deliveryGuidance.Add($"This project only contains send-direction pipeline(s) ({project.SendPipelineCount}), so the generated fetch step is a placeholder: the real message source is whichever flow or orchestration routed messages to this send port in BizTalk.");
        }
    }

    /// <summary>
    /// Decides whether a BizTalk project represents real message-handling behavior worth turning into a draft
    /// flow, or is a shared library (schemas/types only) that should just be inventoried. Orchestrations, maps,
    /// and pipelines all represent genuine behavior; so does a binding file that names this project. A project
    /// with none of those yields no candidate even when it is the only project in scope - a pure schema library
    /// analyzed in isolation legitimately has nothing to migrate, and inventing a placeholder flow for it only
    /// produces noise the reviewer has to recognize and discard.
    /// </summary>
    /// <remarks>
    /// Internal (rather than private) so <c>Service.Tests</c> can regression-test this heuristic directly.
    /// </remarks>
    internal static bool ShouldCreateFlowCandidate(BizTalkProjectResponse project, IReadOnlyList<BindingFileAnalysis> bindingFiles)
    {
        if (project.OrchestrationCount > 0 || project.MapCount > 0 || project.PipelineCount > 0)
        {
            return true;
        }

        return bindingFiles.Any(binding => IsBindingRelatedToProject(binding, project));
    }

    /// <summary>File name glob patterns recognized as BizTalk binding exports (BindingInfo.xml).</summary>
    private static readonly string[] BindingFilePatterns = ["*.BindingInfo.xml", "*Binding*.xml"];

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

    private static BizTalkDraftStepResponse CreateFetchStep(
        string flowId,
        IReadOnlyList<BindingFileAnalysis> bindings,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var portLocation = bindings
            .SelectMany(static binding => binding.ReceivePorts)
            .SelectMany(port => port.ReceiveLocations.Select(location => (Port: port, Location: location)))
            .FirstOrDefault();
        if (portLocation.Location is null)
        {
            var placeholderPath = AddRequirement(requirements, flowId, "fetch.settings.path", "Config", "Choose the staging repository path used when no BizTalk receive location could be imported.", appliedToDraft: true);
            draftWarnings.Add("No BizTalk receive location was matched to this project, so the draft uses a repository inbox placeholder for fetch.");
            AddArchiveAfterProcessingWarning(draftWarnings);
            return new BizTalkDraftStepResponse(
                "document-repository-fetch",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["path"] = placeholderPath,
                    ["searchPattern"] = "*.*",
                    ["includeMetadataSidecars"] = "true",
                    ["afterProcessing"] = nameof(Mulse.Modules.FetchPostProcessingAction.MoveToArchive)
                });
        }

        var fetchStep = CreateFetchSettings(portLocation.Location, flowId, includeConfigPlaceholders: true, requirements, "fetch.settings");
        if (fetchStep.Settings.ContainsKey("afterProcessing"))
        {
            AddArchiveAfterProcessingWarning(draftWarnings);
        }

        if (portLocation.Port.IsTwoWay)
        {
            var twoWaySettings = new Dictionary<string, string>(fetchStep.Settings, StringComparer.OrdinalIgnoreCase)
            {
                ["expectsResponse"] = "true"
            };
            fetchStep = fetchStep with { Settings = twoWaySettings };
            draftWarnings.Add($"Receive port '{portLocation.Port.Name}' is a two-way (request-response) service. Mulse's fetch modules are currently fire-and-forget, so this draft cannot yet return a synchronous reply to the caller - plan a response-capable fetch module (or an inline synchronous augment) before enabling this flow.");
        }

        return fetchStep;
    }

    private static BizTalkDraftStepResponse CreateParseStep(
        string flowId,
        BizTalkProjectResponse project,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var module = SelectParseModule(project);
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.Equals(module, "xml-envelope-debatch-parse", StringComparison.OrdinalIgnoreCase))
        {
            settings["bodyXPath"] = AddRequirement(requirements, flowId, "parse.settings.bodyXPath", "Config", "Enter the XPath selecting each repeated business-message element, based on the imported pipeline's EnvelopeSpecNames schema.", appliedToDraft: true);
            draftWarnings.Add("This project's receive pipeline configures an envelope schema (EnvelopeSpecNames), so the draft uses an envelope-debatching parse step. Set bodyXPath to match the real envelope shape before enabling the flow.");
        }
        else if (string.Equals(module, "xml-xsd-parse", StringComparison.OrdinalIgnoreCase))
        {
            var schemaPaths = AllArtifacts(project)
                .Where(static artifact => string.Equals(artifact.Kind, "Schema", StringComparison.OrdinalIgnoreCase))
                .Select(static artifact => artifact.FullPath)
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

        AddControlFlowAugments(flowId, project, augments, requirements, draftWarnings);

        return augments;
    }

    private static readonly IReadOnlyList<string> StatefulOrchestrationShapeTypes =
    [
        "Listen", "CorrelationDeclaration", "CorrelationType", "Loop", "AtomicTransaction"
    ];

    /// <summary>
    /// Scaffolds draft augment steps from detected orchestration control-flow signals (see
    /// <see cref="DetectOrchestrationControlFlowSignalsByOrchestration"/>) instead of only surfacing them as
    /// open questions. Decision/branch shapes become a <c>decision-augment</c> step, pre-populated with real
    /// rules auto-translated from the orchestration's simple branch expressions (see
    /// <see cref="BizTalkOrchestrationDecisionAnalyzer"/>) and falling back to a config placeholder only when no
    /// branch could be translated. Convoy, correlation, loop, and atomic-transaction shapes become a placeholder
    /// <c>stateful-orchestration-augment</c> step; the matching scaffolded C# module carries the details that
    /// can't be expressed declaratively.
    /// </summary>
    private static void AddControlFlowAugments(
        string flowId,
        BizTalkProjectResponse project,
        List<BizTalkDraftStepResponse> augments,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        if (project.OrchestrationControlFlowSignals.Count == 0)
        {
            return;
        }

        var signalsByShape = project.OrchestrationControlFlowSignals.ToDictionary(static signal => signal.ShapeType, StringComparer.Ordinal);

        if (signalsByShape.TryGetValue("Decision", out var decisionSignal))
        {
            var generatedDecisionJson = BizTalkOrchestrationDecisionAnalyzer.MergeDecisionJson(project.OrchestrationDecisionAnalyses);
            var complexBranchCount = project.OrchestrationDecisionAnalyses.Sum(static analysis => analysis.ComplexBranches.Count);

            string decisionJson;
            if (generatedDecisionJson is not null)
            {
                decisionJson = generatedDecisionJson;
                draftWarnings.Add($"Detected {decisionSignal.Count} {decisionSignal.Description} in the source orchestration. Branch conditions simple enough to translate were auto-converted into real decision-augment rules that tag the matched branch via metadata ('biztalk:<decision>' = '<branch>'); they do not reproduce each branch's side effects, so review them and wire up the downstream behavior before enabling the flow.");
                if (complexBranchCount > 0)
                {
                    draftWarnings.Add($"{complexBranchCount} decision branch(es) were too complex to auto-translate (method calls, non-equality operators, or assignments). Their original BizTalk expressions are quoted in the scaffolded C# module for this orchestration.");
                }
            }
            else
            {
                decisionJson = AddRequirement(requirements, flowId, "augments.decision.decisionJson", "Config", $"Detected {decisionSignal.Count} {decisionSignal.Description} in the orchestration. Replace the placeholder decision rules with the real branch conditions before enabling the flow.", appliedToDraft: true);
                draftWarnings.Add($"Detected {decisionSignal.Count} {decisionSignal.Description} in the source orchestration, but none of the branch expressions were simple enough to auto-translate. A decision-augment step was scaffolded with placeholder rules; replace them with the real branch conditions.");
            }

            augments.Add(new BizTalkDraftStepResponse(
                "decision-augment",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["decisionJson"] = decisionJson,
                    ["stopAfterFirstMatch"] = "true"
                }));
        }

        var statefulSignals = StatefulOrchestrationShapeTypes
            .Select(shapeType => signalsByShape.GetValueOrDefault(shapeType))
            .Where(static signal => signal is not null)
            .Select(static signal => signal!)
            .ToArray();
        if (statefulSignals.Length > 0)
        {
            var correlationPath = AddRequirement(requirements, flowId, "augments.stateful.correlationPath", "Config", "Set the metadata key or JSONPath that carries the correlation value used by the orchestration's correlation set(s).", appliedToDraft: true);
            augments.Add(new BizTalkDraftStepResponse(
                "stateful-orchestration-augment",
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["mode"] = "ResumeOrWait",
                    ["correlationSource"] = "Metadata",
                    ["correlationPath"] = correlationPath
                }));
            var summary = string.Join(", ", statefulSignals.Select(signal => $"{signal.Count} {signal.Description}"));
            draftWarnings.Add($"Detected {summary} in the source orchestration. A stateful-orchestration-augment step was scaffolded to model convoy/correlation waits; configure the real correlation source, bookmarks, and state capture before enabling the flow.");
        }
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

        return ApplyAtomicScopeIfDetected(flowId, project, routes, requirements, draftWarnings);
    }

    /// <summary>
    /// Tags all delivery routes with a shared atomic scope, and adds a compensation-module requirement for each,
    /// when the source orchestration used a BizTalk <c>AtomicTransaction</c> scope and there is more than one
    /// delivery route to roll back between. With a single route there is nothing to compensate, so tagging is
    /// skipped. This closes the loop between <see cref="DetectOrchestrationControlFlowSignalsByOrchestration"/> and the
    /// <see cref="Mulse.Modules.DeliveryRouteDefinition.AtomicScope"/>/<see cref="Mulse.Modules.DeliveryRouteDefinition.Compensation"/>
    /// runtime model: the draft flow is pre-wired for all-or-nothing delivery, and the operator only needs to
    /// supply real compensation module settings before enabling it.
    /// </summary>
    private static IReadOnlyList<BizTalkDraftDeliveryRouteResponse> ApplyAtomicScopeIfDetected(
        string flowId,
        BizTalkProjectResponse project,
        List<BizTalkDraftDeliveryRouteResponse> routes,
        List<BizTalkSettingRequirementResponse> requirements,
        List<string> draftWarnings)
    {
        var hasAtomicTransaction = project.OrchestrationControlFlowSignals
            .Any(static signal => string.Equals(signal.ShapeType, "AtomicTransaction", StringComparison.Ordinal));
        if (!hasAtomicTransaction || routes.Count < 2)
        {
            return routes;
        }

        const string atomicScope = "atomic-scope-1";
        var scopedRoutes = new List<BizTalkDraftDeliveryRouteResponse>(routes.Count);
        for (var index = 0; index < routes.Count; index++)
        {
            AddRequirement(requirements, flowId, $"deliveries[{index}].compensation.module", "Config", $"This delivery route participates in a migrated atomic transaction scope. Configure a compensation deliver module (e.g. a cancel/undo call) for deliveries[{index}], or leave unset to only log a warning if rollback is needed.", appliedToDraft: false);            scopedRoutes.Add(routes[index] with
            {
                AtomicScope = atomicScope,
                Compensation = new BizTalkDraftStepResponse(string.Empty, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))
            });
        }

        draftWarnings.Add($"The source orchestration used an AtomicTransaction scope, so all {routes.Count} delivery routes were tagged with atomic scope '{atomicScope}'. Configure a real compensation deliver module for each route before enabling the flow, or rollback will only be logged rather than executed.");
        return scopedRoutes;
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

        if (deliverSettings.Retry is { } importedRetry)
        {
            draftWarnings.Add($"Send port '{sendPort.Name}' had a native BizTalk retry configuration (retry {sendPort.RetryCount} time(s), {sendPort.RetryIntervalMinutes} minute(s) apart) - the deliver step was migrated with an equivalent retry policy ({importedRetry.MaxAttempts} max attempts, {importedRetry.Delay} delay). Review before enabling the flow.");
        }

        if (sendPort.IsTwoWay)
        {
            var twoWaySettings = new Dictionary<string, string>(deliverSettings.Settings, StringComparer.OrdinalIgnoreCase)
            {
                ["expectsResponse"] = "true"
            };
            deliverSettings = deliverSettings with { Settings = twoWaySettings };
            draftWarnings.Add($"Send port '{sendPort.Name}' is a two-way (solicit-response) WCF port. Mulse's deliver modules are currently fire-and-forget, so the synchronous reply from '{sendPort.Address}' is not captured yet - plan a response-capturing deliver module (or a follow-up augment step) before enabling this route.");
        }

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
                var generatedStylesheet = project.MapAnalyses.FirstOrDefault(static analysis => analysis.GeneratedXsltSourceCode is not null);
                settings["xsltPath"] = AddRequirement(
                    requirements,
                    referencePrefix,
                    $"{settingPrefix}.xsltPath",
                    "Config",
                    generatedStylesheet is null
                        ? "Convert the BizTalk map or BTM asset into an XSLT file and update this path before enabling the flow."
                        : $"A starter XSLT stylesheet ('{generatedStylesheet.ScaffoldedModuleFileName}') was auto-generated from map '{generatedStylesheet.MapName}' ({generatedStylesheet.DirectLinkCount} direct link(s), {generatedStylesheet.ConstantValueCount} constant(s), {generatedStylesheet.FunctoidInvolvedLinkCount} functoid-involved link(s) left untranslated). Download it from the scaffolded-module endpoints, save it, then set this path to the saved file.",
                    appliedToDraft: true);
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
                settings.TryAdd("deleteAfterRead", "true");
                break;
            case "file-system-fetch":
                settings["path"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.path", address, "File-system fetch path imported from BizTalk should usually be externalized to configuration.", includeConfigPlaceholders);
                settings["searchPattern"] = "*.*";
                settings["afterProcessing"] = nameof(Mulse.Modules.FetchPostProcessingAction.MoveToArchive);
                break;
            case "http-inbound-fetch":
                settings["route"] = ResolveConfigValue(requirements, referencePrefix, $"{settingPathPrefix}.route", CreateSlug(location.Name), "Inbound route segment imported from a BizTalk WCF/HTTP receive location. Exposed at /api/inbound/{route}; the original endpoint must be repointed here during cutover.", includeConfigPlaceholders);
                break;
            default:
                settings["path"] = AddRequirement(requirements, referencePrefix, $"{settingPathPrefix}.path", "Config", "Choose the staging repository path used to replace this BizTalk receive transport.", appliedToDraft: includeConfigPlaceholders, actualValue: address);
                settings["searchPattern"] = "*.*";
                settings["includeMetadataSidecars"] = "true";
                settings["afterProcessing"] = nameof(Mulse.Modules.FetchPostProcessingAction.MoveToArchive);
                break;
        }

        return new BizTalkDraftStepResponse(module, settings);
    }

    /// <summary>
    /// BizTalk receive locations always remove (or move) a file after successful pickup, specifically to
    /// avoid re-processing it on the next poll. Imported fetch steps default to the same behavior
    /// (archiving to a sibling '&lt;path&gt;.processed' folder) rather than silently leaving files in place,
    /// which would otherwise deliver duplicate payloads on every subsequent run of a timer-triggered flow.
    /// </summary>
    private static void AddArchiveAfterProcessingWarning(List<string> draftWarnings)
    {
        draftWarnings.Add("The fetch step defaults to archiving files after a successful run (afterProcessing=MoveToArchive) so the same file isn't re-delivered on every run, matching BizTalk's receive-location behavior. Change 'afterProcessing' to 'None' if you need files left in place for manual inspection.");
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

        return new BizTalkDraftStepResponse(module, settings, CreateRetryPolicy(sendPort));
    }

    /// <summary>
    /// Translates a BizTalk send port's native retry configuration (<c>RetryCount</c>/<c>RetryInterval</c>,
    /// read straight from the binding file) into an equivalent <see cref="RetryPolicyDefinition"/> for the
    /// migrated deliver step, so imported flows keep the source system's retry intent instead of silently
    /// defaulting to no retries. BizTalk's RetryCount is the number of retries *after* the first attempt and
    /// RetryInterval is in minutes; a RetryCount of 0 means BizTalk itself was configured with no retries, so
    /// no policy (null, i.e. exactly one attempt) is generated in that case.
    /// </summary>
    private static RetryPolicyDefinition? CreateRetryPolicy(SendPortAnalysis sendPort)
    {
        if (sendPort.RetryCount <= 0)
        {
            return null;
        }

        return new RetryPolicyDefinition
        {
            MaxAttempts = sendPort.RetryCount + 1,
            Delay = TimeSpan.FromMinutes(Math.Max(sendPort.RetryIntervalMinutes, 0)),
            Backoff = RetryBackoffKind.Fixed
        };
    }

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
                Path.GetRelativePath(rootDirectory, fullPath),
                fullPath));
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

    private static BizTalkProjectResponse CreateDefaultProjectProfile()
        => new("ImportedBinding", string.Empty, 0, 0, 1, 0, [], []);

    internal sealed record AnalysisScope(
        string SourcePath,
        string SourceType,
        string DisplayName,
        string RootDirectory,
        IReadOnlyList<string> BizTalkProjects,
        IReadOnlyList<string> CustomProjects);

    /// <summary>
    /// Identifies one orchestration's slice of a multi-orchestration BizTalk project when that project is split
    /// into one draft flow candidate per orchestration.
    /// </summary>
    /// <param name="OrchestrationName">The orchestration file name (without extension) this candidate covers.</param>
    /// <param name="TotalOrchestrations">How many orchestrations the source project contains in total.</param>
    internal sealed record OrchestrationSplit(string OrchestrationName, int TotalOrchestrations);

    internal sealed record SolutionProjectSet(
        IReadOnlyList<string> BizTalkProjects,
        IReadOnlyList<string> CustomProjects);

    internal sealed record BizTalkProjectAnalysis(BizTalkProjectResponse Response, IReadOnlyList<string> References);

    internal sealed record BindingFileAnalysis(
        string Name,
        string FilePath,
        IReadOnlyList<ReceivePortAnalysis> ReceivePorts,
        IReadOnlyList<SendPortAnalysis> SendPorts);

    internal sealed record ReceivePortAnalysis(
        string Name,
        bool IsTwoWay,
        IReadOnlyList<ReceiveLocationAnalysis> ReceiveLocations);

    internal sealed record ReceiveLocationAnalysis(
        string Name,
        string TransportType,
        string Address,
        string ReceivePipeline,
        IReadOnlyDictionary<string, string> TransportProperties);

    internal sealed record SendPortAnalysis(
        string Name,
        string Description,
        string TransportType,
        string Address,
        string TransmitPipeline,
        string ReceivePipeline,
        string FilterExpression,
        bool IsTwoWay,
        IReadOnlyDictionary<string, string> TransportProperties,
        int RetryCount = 0,
        int RetryIntervalMinutes = 0);
}
