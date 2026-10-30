using Mulse.Modules.Pipeline;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static FlowMigrationReport CreateMigrationReport(
        BizTalkProjectAnalysis project,
        OrchestrationSplit? split,
        IReadOnlyList<BizTalkCustomAssemblyResponse> assemblies,
        IReadOnlyList<BindingFileAnalysis> bindings,
        IReadOnlyList<BizTalkSettingRequirementResponse> requirements,
        IReadOnlyList<string> warnings)
    {
        var response = project.Response;
        var artifacts = response.Artifacts.Concat(response.ReferencedArtifacts)
            .Where(artifact => !string.Equals(artifact.Kind, "Orchestration", StringComparison.OrdinalIgnoreCase)
                || (response.Artifacts.Contains(artifact)
                    && (split is null || string.Equals(OrchestrationName(artifact), split.OrchestrationName, StringComparison.OrdinalIgnoreCase))))
            .DistinctBy(static artifact => artifact.FullPath, StringComparer.OrdinalIgnoreCase)
            .Select(ReportArtifact).ToList();
        artifacts.AddRange(assemblies.Select(assembly => new MigrationArtifactReport("CustomAssembly", assembly.ProjectPath,
            MigrationTranslationStatus.Unsupported,
            [Diagnostic("BT-CUSTOM-CODE", MigrationDiagnosticSeverity.Blocking, assembly.ProjectPath,
                "Custom assembly source was inventoried, not translated or executed.",
                "Implement and register equivalent native Mulse behavior, then verify it with synthetic inputs and expected outputs.")])));
        artifacts.AddRange(bindings.Select(binding => new MigrationArtifactReport("Binding", binding.FilePath,
            MigrationTranslationStatus.ReviewRequired,
            [Diagnostic("BT-BINDING-REVIEW", MigrationDiagnosticSeverity.Review, binding.FilePath,
                "Transport settings were inferred from a related binding export; exact port/subscription ownership and behavior are not verified.",
                "Review the receive/send ports, filters, retries, request/reply behavior, and selected Mulse modules.")])));

        var diagnostics = new List<MigrationDiagnostic>
        {
            Diagnostic("BT-SEMANTICS-UNVERIFIED", MigrationDiagnosticSeverity.Blocking, response.ProjectPath,
                "This draft has not been verified against BizTalk behavior. Artifact analysis does not prove executable equivalence.",
                "Complete all required translations and compare a local synthetic-input run with trusted expected output before enabling or running.")
        };
        diagnostics.AddRange(project.DependencyWarnings.Select(warning => Diagnostic("BT-DEPENDENCY-UNRESOLVED",
            MigrationDiagnosticSeverity.Blocking, response.ProjectPath, warning,
            "Supply the required source projects and concrete reference paths, then reanalyze the workspace.")));
        if (bindings.Count == 0)
        {
            diagnostics.Add(Diagnostic("BT-BINDING-MISSING", MigrationDiagnosticSeverity.Blocking, response.ProjectPath,
                "No related binding export was found; transport stages use migration placeholders.",
                "Supply the deployed binding export or configure and verify the equivalent transport stages manually."));
        }
        diagnostics.AddRange(requirements.Select(requirement => Diagnostic("BT-CONFIGURATION-REQUIRED",
            requirement.AppliedToDraft ? MigrationDiagnosticSeverity.Blocking : MigrationDiagnosticSeverity.Review,
            response.ProjectPath,
            $"{requirement.Kind} reference '{requirement.ReferenceName}' for '{requirement.SettingPath}' requires configuration or review.",
            "Resolve the reference on the Config values page and review the associated module settings. This import-time diagnostic is not automatically cleared.")));

        var ownOrchestrations = OrchestrationArtifacts(response.Artifacts).ToArray();
        var sourceOrchestration = split?.OrchestrationName
            ?? (ownOrchestrations.Length == 1 ? OrchestrationName(ownOrchestrations[0]) : null);
        return new FlowMigrationReport("BizTalk", response.ProjectPath, sourceOrchestration,
            artifacts, diagnostics, warnings.ToArray());

        MigrationArtifactReport ReportArtifact(BizTalkArtifactResponse artifact)
        {
            var path = artifact.FullPath;
            var status = MigrationTranslationStatus.ReviewRequired;
            var issues = new List<MigrationDiagnostic>();
            if (!File.Exists(path))
            {
                return new(artifact.Kind, path, MigrationTranslationStatus.Unsupported,
                    [Diagnostic("BT-ARTIFACT-MISSING", MigrationDiagnosticSeverity.Blocking, path,
                        "The project lists an artifact that is not present on disk.", "Restore the original artifact and reanalyze the project.")]);
            }

            switch (artifact.Kind)
            {
                case "Map":
                    var map = response.MapAnalyses.Concat(project.ReferencedMapAnalyses)
                        .FirstOrDefault(map => string.Equals(map.MapFilePath, path, StringComparison.OrdinalIgnoreCase));
                    if (map is null)
                    {
                        status = MigrationTranslationStatus.Unsupported;
                        issues.Add(Diagnostic("BT-MAP-NOT-ANALYZED", MigrationDiagnosticSeverity.Blocking, path,
                            "The map analyzer did not produce a result; the map could not be read or its format was not recognized.",
                            "Inspect the source map, resolve access/format errors, and reanalyze; do not treat it as an empty successful map."));
                    }
                    else
                    {
                        if (map.FunctoidInvolvedLinkCount > 0 || map.FunctoidFidCounts.Count > 0 || map.GeneratedXsltSourceCode is null)
                        {
                            status = MigrationTranslationStatus.Unsupported;
                            issues.Add(Diagnostic("BT-MAP-UNTRANSLATED", MigrationDiagnosticSeverity.Blocking, path,
                                $"{map.FunctoidInvolvedLinkCount} unsupported/functoid links and {map.FunctoidFidCounts.Values.Sum()} functoid instances were detected; this map is not fully translated.",
                                "Implement the remaining transformations, including script functoids, and validate every required mapping."));
                        }
                        if (map.GeneratedXsltSourceCode is not null)
                        {
                            issues.Add(Diagnostic("BT-MAP-NOT-WIRED", MigrationDiagnosticSeverity.Blocking, path,
                                $"Starter XSLT covers {map.DirectLinkCount} direct links and {map.ConstantValueCount} constants, but is not installed or wired into the draft.",
                                "Review namespaces, repetition, nulls and XPath semantics, install the stylesheet, configure the render step, and compare expected output."));
                        }
                    }
                    break;
                case "Orchestration":
                    var analysis = response.OrchestrationDecisionAnalyses.FirstOrDefault(analysis =>
                        string.Equals(analysis.OrchestrationName, OrchestrationName(artifact), StringComparison.OrdinalIgnoreCase));
                    if (analysis?.ScaffoldedModuleSourceCode is not null)
                    {
                        status = MigrationTranslationStatus.StubGenerated;
                        issues.Add(Diagnostic("BT-ORCHESTRATION-STUB", MigrationDiagnosticSeverity.Blocking, path,
                            "A starter C# module was generated for untranslated control flow. A compiling or pass-through stub is not an implementation.",
                            "Implement and register the missing workflow behavior; verify ordering, branches, waits, correlation, and failure paths."));
                    }
                    else
                    {
                        issues.Add(Diagnostic("BT-ORCHESTRATION-REVIEW", MigrationDiagnosticSeverity.Blocking, path,
                            "Only recognized decision expressions may have been translated; complete orchestration semantics were not verified.",
                            "Review message assignments, receives/sends, control flow, and exceptions against the original orchestration."));
                    }
                    break;
                case "Pipeline":
                    status = MigrationTranslationStatus.Unsupported;
                    issues.Add(Diagnostic("BT-PIPELINE-UNTRANSLATED", MigrationDiagnosticSeverity.Blocking, path,
                        "Pipeline direction and module-selection hints were analyzed, not the complete pipeline component behavior.",
                        "Implement equivalent decoding, parsing, context promotion, preprocessing, and multipart behavior using native Mulse modules."));
                    break;
                case "Schema":
                    issues.Add(Diagnostic("BT-SCHEMA-REVIEW", MigrationDiagnosticSeverity.Review, path,
                        "The schema was inventoried; import does not verify its namespaces, references, annotations, or runtime use.",
                        "Verify schema dependencies and parser/render settings with representative synthetic messages."));
                    break;
                default:
                    status = MigrationTranslationStatus.Unsupported;
                    issues.Add(Diagnostic("BT-ARTIFACT-UNSUPPORTED", MigrationDiagnosticSeverity.Blocking, path,
                        "This artifact kind has no verified translator.", "Implement equivalent behavior and verify it before execution."));
                    break;
            }
            return new(artifact.Kind, path, status, issues);
        }
    }

    private static MigrationDiagnostic Diagnostic(string code, MigrationDiagnosticSeverity severity, string sourcePath,
        string message, string remediation) => new(code, severity, sourcePath, message, remediation);
}
