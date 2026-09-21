using Microsoft.AspNetCore.Http.HttpResults;
using Service.Models;

namespace Service.Endpoints;

public static class DesignerEndpoints
{
    public static WebApplication MapDesignerEndpoints(this WebApplication app)
    {
        app.MapPost("/api/flow-designer/analyze", async Task<Ok<FlowDesignResponse>> (AnalyzeFlowDesignRequest request, IFlowDesignService flowDesignService, CancellationToken cancellationToken) =>
            {
                var response = await flowDesignService.AnalyzeAsync(request, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(response);
            })
            .WithName("AnalyzeFlowDesignSample")
            .WithSummary("Analyze uploaded or pasted sample data")
            .WithDescription("Detects a sample payload format, suggests compatible modules, and proposes field mappings for merge, orchestration, and output targets.")
            .WithTags("Designer")
            .Produces<FlowDesignResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }
}
