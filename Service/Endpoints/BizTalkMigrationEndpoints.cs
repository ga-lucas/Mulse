using System.IO.Compression;
using Microsoft.AspNetCore.Http.HttpResults;
using Service.Models;

namespace Service.Endpoints;

public static class BizTalkMigrationEndpoints
{
    private static readonly System.Text.Json.JsonSerializerOptions ControlFlowJsonOptions = new(System.Text.Json.JsonSerializerDefaults.Web) { WriteIndented = true };

    public static WebApplication MapBizTalkMigrationEndpoints(this WebApplication app)
    {
        app.MapPost("/api/biztalk-import/rules/preview", (XmlRulePreviewRequest request, CancellationToken ct) =>
            TypedResults.Ok(Mulse.Modules.Decisions.XmlRuleRuntime.Evaluate(
                Mulse.Modules.Decisions.XmlRuleRuntime.ReadPolicy(request.PolicyJson), request.InputXml, ct)))
            .WithTags("BizTalk Migration").WithSummary("Preview native XML field rules against supplied synthetic facts; no I/O or secret resolution")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        app.MapPost("/api/biztalk-import/analyze", async Task<Ok<BizTalkSolutionAnalysisResponse>> (AnalyzeBizTalkSolutionRequest request, IBizTalkImportService bizTalkImportService, CancellationToken cancellationToken) =>
            {
                var response = await bizTalkImportService.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(response);
            })
            .WithName("AnalyzeBizTalkSolution")
            .WithSummary("Analyze a BizTalk solution or project for migration")
            .WithDescription("Reads a BizTalk solution, BizTalk project, or directory, inventories the migration surface, imports binding metadata, and returns generated draft Mulse flows for assisted migration.")
            .WithTags("BizTalk Migration")
            .Produces<BizTalkSolutionAnalysisResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/biztalk-import/flows", async Task<Created<FlowResponse>> (CreateImportedBizTalkFlowRequest request, IBizTalkImportService bizTalkImportService, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                var analysis = await bizTalkImportService.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
                {
                    SourcePath = request.SourcePath,
                    AdditionalSourcePaths = request.AdditionalSourcePaths,
                    AdditionalBindingPaths = request.AdditionalBindingPaths,
                    AdditionalRulePaths = request.AdditionalRulePaths,
                    GenerateNativeFlow = request.GenerateNativeFlow
                }, cancellationToken).ConfigureAwait(false);

                var candidate = analysis.FlowCandidates.FirstOrDefault(flow => string.Equals(flow.SuggestedFlowId, request.SuggestedFlowId, StringComparison.OrdinalIgnoreCase))
                    ?? throw new KeyNotFoundException($"No generated BizTalk draft flow with id '{request.SuggestedFlowId}' was found for source '{request.SourcePath}'.");

                var created = await flowDefinitionService.CreateAsync(FlowMappings.MapPipeline(candidate.DraftFlow), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"/api/flows/{created.Id}", FlowMappings.MapFlow(created));
            })
            .WithName("ImportBizTalkDraftFlow")
            .WithSummary("Import a generated BizTalk draft flow")
            .WithDescription("Analyzes a BizTalk source, finds one generated draft flow by suggested id, and creates it in the runtime flow catalog as a disabled Mulse flow for migration review.")
            .WithTags("BizTalk Migration")
            .Produces<FlowResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/biztalk-import/control-flow/download", async Task<FileContentHttpResult> (CreateImportedBizTalkFlowRequest request, IBizTalkImportService bizTalkImportService, CancellationToken cancellationToken) =>
            {
                var candidate = await ResolveFlowCandidateAsync(request, bizTalkImportService, cancellationToken).ConfigureAwait(false);
                var paths = candidate.DraftFlow.Migration?.Artifacts.Where(artifact => artifact.Kind == "Orchestration").Select(artifact => artifact.SourcePath).ToArray() ?? [];
                if (paths.Length != 1) throw new InvalidOperationException("Select a candidate with exactly one owned orchestration before downloading its control-flow source model.");
                var program = await Mulse.Modules.Orchestration.OdxProgramReader.ReadFileAsync(paths[0], cancellationToken).ConfigureAwait(false);
                return TypedResults.File(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(program, ControlFlowJsonOptions),
                    "application/json", $"{candidate.SuggestedFlowId}-control-flow.json");
            })
            .WithName("DownloadBizTalkControlFlow")
            .WithSummary("Download a structured developer ODX source model, including expressions; not executable translation approval")
            .WithDescription("Explicit source-code export preserves shape order, nesting, OIDs, source lines and property values for SDK developer bindings. Unlike the saved names-only migration inventory, it includes source expressions and literals; handle as private source code. Nothing is executed or approved.")
            .WithTags("BizTalk Migration")
            .Produces(StatusCodes.Status200OK, contentType: "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPost("/api/biztalk-import/scaffolded-modules/download", async Task<Results<FileContentHttpResult, NotFound<string>>> (CreateImportedBizTalkFlowRequest request, IBizTalkImportService bizTalkImportService, CancellationToken cancellationToken) =>
            {
                var candidate = await ResolveFlowCandidateAsync(request, bizTalkImportService, cancellationToken).ConfigureAwait(false);
                if (candidate.ScaffoldedModules.Count == 0)
                {
                    return TypedResults.NotFound($"Flow candidate '{request.SuggestedFlowId}' has no scaffolded modules to download.");
                }

                var zipBytes = CreateScaffoldedModulesZip(candidate.ScaffoldedModules);
                return TypedResults.File(zipBytes, "application/zip", $"{request.SuggestedFlowId}-scaffolded-modules.zip");
            })
            .WithName("DownloadBizTalkScaffoldedModules")
            .WithSummary("Download scaffolded migration artifacts for a generated BizTalk draft flow as a zip")
            .WithDescription("Re-analyzes the source and downloads starter C# modules, automatically bound ODX source, per-node translation reports and partial XSLT scaffolds. ODX C# embeds original expressions/literals: handle as private source code. Unsupported bindings fail before execution; compiling or registering artifacts does not approve imported execution. Pipeline inventory excludes configuration values.")
            .WithTags("BizTalk Migration")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/biztalk-import/scaffolded-modules/{moduleId}/download", async Task<Results<FileContentHttpResult, NotFound<string>>> (string moduleId, CreateImportedBizTalkFlowRequest request, IBizTalkImportService bizTalkImportService, CancellationToken cancellationToken) =>
            {
                var candidate = await ResolveFlowCandidateAsync(request, bizTalkImportService, cancellationToken).ConfigureAwait(false);
                var module = candidate.ScaffoldedModules.FirstOrDefault(module => string.Equals(module.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase));
                if (module is null)
                {
                    return TypedResults.NotFound($"Flow candidate '{request.SuggestedFlowId}' has no scaffolded module with id '{moduleId}'.");
                }

                var bytes = System.Text.Encoding.UTF8.GetBytes(module.SourceCode);
                return TypedResults.File(bytes, "text/plain", module.FileName);
            })
            .WithName("DownloadBizTalkScaffoldedModule")
            .WithSummary("Download one scaffolded migration artifact for a generated BizTalk draft flow")
            .WithDescription("Re-analyzes the source and returns one C# module, ODX translation report or partial map stylesheet by artifact id. Automatically bound ODX C# embeds original expressions/literals and must be handled as private source code. Unsupported bindings fail explicitly; no artifact download approves imported execution.")
            .WithTags("BizTalk Migration")
            .Produces(StatusCodes.Status200OK, contentType: "text/plain")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .Produces<string>(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }

    /// <summary>
    /// Re-runs BizTalk analysis for the given source path and resolves the single generated draft flow
    /// candidate matching <see cref="CreateImportedBizTalkFlowRequest.SuggestedFlowId"/>. Shared by the import
    /// and scaffolded-module-download endpoints, all of which stay stateless by recomputing analysis rather than
    /// caching it, matching the existing import endpoint's behavior.
    /// </summary>
    private static async Task<BizTalkFlowCandidateResponse> ResolveFlowCandidateAsync(
        CreateImportedBizTalkFlowRequest request,
        IBizTalkImportService bizTalkImportService,
        CancellationToken cancellationToken)
    {
        var analysis = await bizTalkImportService.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        {
            SourcePath = request.SourcePath,
            AdditionalSourcePaths = request.AdditionalSourcePaths,
            AdditionalBindingPaths = request.AdditionalBindingPaths,
            AdditionalRulePaths = request.AdditionalRulePaths,
            GenerateNativeFlow = request.GenerateNativeFlow
        }, cancellationToken).ConfigureAwait(false);

        return analysis.FlowCandidates.FirstOrDefault(flow => string.Equals(flow.SuggestedFlowId, request.SuggestedFlowId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No generated BizTalk draft flow with id '{request.SuggestedFlowId}' was found for source '{request.SourcePath}'.");
    }

    /// <summary>Zips every scaffolded module's source code into an in-memory archive keyed by its file name.</summary>
    private static byte[] CreateScaffoldedModulesZip(IReadOnlyList<BizTalkScaffoldedModuleResponse> modules)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var module in modules)
            {
                var entry = archive.CreateEntry(module.FileName, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                using var writer = new StreamWriter(entryStream);
                writer.Write(module.SourceCode);
            }
        }

        return stream.ToArray();
    }
}

