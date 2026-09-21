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
                    SourcePath = request.SourcePath
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

        return app;
    }
}
