using Microsoft.AspNetCore.Http.HttpResults;
using Mulse.Modules;
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
            .WithDescription("Detects a sample payload format, suggests compatible fetch, parse, augment, render, and deliver modules, and proposes field mappings for downstream routes.")
            .WithTags("Designer")
            .Produces<FlowDesignResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/flow-designer/flows", async Task<Created<FlowResponse>> (CreateDesignedFlowRequest request, IFlowDesignService flowDesignService, CancellationToken cancellationToken) =>
            {
                var created = await flowDesignService.CreateFlowAsync(request, cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"/api/flows/{created.Id}", FlowMappings.MapFlow(created));
            })
            .WithName("CreateFlowFromDesigner")
            .WithSummary("Create a flow from the WYSIWYG designer")
            .WithDescription("Creates a runtime-configurable flow from a designer-authored module selection, settings, and visual field mapping document.")
            .WithTags("Designer")
            .Produces<FlowResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }
}
