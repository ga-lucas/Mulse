using System.IO.Compression;
using Microsoft.AspNetCore.Http.HttpResults;
using Service.Models;

namespace Service.Endpoints;

public static class BizTalkMigrationEndpoints
{
    public static WebApplication MapBizTalkMigrationEndpoints(this WebApplication app)
    {
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
                    AdditionalSourcePaths = request.AdditionalSourcePaths
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
            .WithSummary("Download all scaffolded C# modules for a generated BizTalk draft flow as a zip")
            .WithDescription("Re-analyzes the BizTalk source, finds one generated draft flow by suggested id, and returns every starter C# module scaffolded for its complex decision/control-flow logic (correlations, convoys, transactions, loops) as a downloadable zip. Each file compiles as a safe pass-through with TODO comments quoting the original BizTalk logic.")
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
            .WithSummary("Download one scaffolded C# module for a generated BizTalk draft flow")
            .WithDescription("Re-analyzes the BizTalk source, finds one generated draft flow by suggested id, and returns a single starter C# module (identified by its module id) as a downloadable .cs file.")
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
            AdditionalSourcePaths = request.AdditionalSourcePaths
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

