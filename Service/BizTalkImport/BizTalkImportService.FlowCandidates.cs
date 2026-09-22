using System.Xml.Linq;
using Mulse.Modules;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
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
}
