using Microsoft.AspNetCore.Http.HttpResults;
using Mulse.Modules;
using Service.Models;

namespace Service.Endpoints;

public static class FlowEndpoints
{
    public static WebApplication MapFlowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/flows", Ok<FlowResponse[]> (IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = flowDefinitionService.GetAll()
                    .Select(MapFlow)
                    .ToArray();
                return TypedResults.Ok(response);
            })
            .WithName("GetFlows")
            .WithSummary("List configured flows")
            .WithDescription("Returns the current integration flows loaded from the runtime configuration store, including input, orchestration augment, and output module references.")
            .WithTags("Flows")
            .Produces<FlowResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapGet("/api/flows/{flowId}", Results<Ok<FlowResponse>, NotFound> (string flowId, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var flow = flowDefinitionService.GetAll().FirstOrDefault(candidate => string.Equals(candidate.Id, flowId, StringComparison.OrdinalIgnoreCase));
                return flow is null ? TypedResults.NotFound() : TypedResults.Ok(MapFlow(flow));
            })
            .WithName("GetFlowById")
            .WithSummary("Get a configured flow")
            .WithDescription("Returns a single runtime-configurable flow by id.")
            .WithTags("Flows")
            .Produces<FlowResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/flows", async Task<Created<FlowResponse>> (CreateFlowRequest request, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                var created = await flowDefinitionService.CreateAsync(MapPipeline(request), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"/api/flows/{created.Id}", MapFlow(created));
            })
            .WithName("CreateFlow")
            .WithSummary("Create a runtime-configurable flow")
            .WithDescription("Creates a new flow in the runtime configuration store without restarting the service.")
            .WithTags("Flows")
            .Produces<FlowResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPut("/api/flows/{flowId}", async Task<Ok<FlowResponse>> (string flowId, UpdateFlowRequest request, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                var updated = await flowDefinitionService.UpdateAsync(flowId, MapPipeline(request), cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(MapFlow(updated));
            })
            .WithName("UpdateFlow")
            .WithSummary("Update a runtime-configurable flow")
            .WithDescription("Updates an existing flow in the runtime configuration store without restarting the service.")
            .WithTags("Flows")
            .Produces<FlowResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapDelete("/api/flows/{flowId}", async Task<NoContent> (string flowId, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                await flowDefinitionService.DeleteAsync(flowId, cancellationToken).ConfigureAwait(false);
                return TypedResults.NoContent();
            })
            .WithName("DeleteFlow")
            .WithSummary("Delete a runtime-configurable flow")
            .WithDescription("Deletes a flow from the runtime configuration store.")
            .WithTags("Flows")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/flows/{flowId}/run", async Task<Ok<FlowRunResponse>> (string flowId, IFlowRuntime flowRuntime, CancellationToken cancellationToken) =>
            {
                var result = await flowRuntime.ExecuteAsync(flowId, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new FlowRunResponse(
                    result.FlowId,
                    result.StartedAt,
                    result.CompletedAt,
                    result.PayloadCount,
                    result.OutputModules));
            })
            .WithName("RunFlow")
            .WithSummary("Run a configured flow")
            .WithDescription("Executes a configured flow immediately, regardless of whether it is usually interval-driven.")
            .WithTags("Flows")
            .Produces<FlowRunResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }

    private static FlowResponse MapFlow(PipelineDefinition flow)
    {
        return new FlowResponse(
            flow.Id,
            flow.Enabled,
            flow.Trigger.Mode,
            flow.Trigger.Interval,
            flow.Trigger.RunOnStartup,
            flow.Input.Module,
            flow.Augments.Select(static step => step.Module).ToArray(),
            flow.Outputs.Select(static step => step.Module).ToArray(),
            MapStep(flow.Input),
            flow.Augments.Select(MapStep).ToArray(),
            flow.Outputs.Select(MapStep).ToArray());
    }

    private static PipelineDefinition MapPipeline(CreateFlowRequest request)
    {
        return new PipelineDefinition
        {
            Id = request.Id,
            Enabled = request.Enabled,
            Trigger = new PipelineTriggerOptions
            {
                Mode = request.Trigger.Mode,
                Interval = request.Trigger.Interval,
                RunOnStartup = request.Trigger.RunOnStartup
            },
            Input = MapStep(request.Input),
            Augments = request.Augments.Select(MapStep).ToList(),
            Outputs = request.Outputs.Select(MapStep).ToList()
        };
    }

    private static PipelineDefinition MapPipeline(UpdateFlowRequest request)
    {
        return new PipelineDefinition
        {
            Id = request.Id,
            Enabled = request.Enabled,
            Trigger = new PipelineTriggerOptions
            {
                Mode = request.Trigger.Mode,
                Interval = request.Trigger.Interval,
                RunOnStartup = request.Trigger.RunOnStartup
            },
            Input = MapStep(request.Input),
            Augments = request.Augments.Select(MapStep).ToList(),
            Outputs = request.Outputs.Select(MapStep).ToList()
        };
    }

    private static ModuleStepResponse MapStep(ModuleStepDefinition step)
    {
        return new ModuleStepResponse(step.Module, new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase));
    }

    private static ModuleStepDefinition MapStep(FlowStepRequest step)
    {
        return new ModuleStepDefinition
        {
            Module = step.Module,
            Settings = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase)
        };
    }
}
