using Microsoft.AspNetCore.Http.HttpResults;
using Mulse.Modules;
using Mulse.Modules.Payloads;
using Service.Models;

namespace Service.Endpoints;

public static class FlowEndpoints
{
    public static WebApplication MapFlowEndpoints(this WebApplication app)
    {
        app.MapGet("/api/flows", Ok<FlowResponse[]> (IFlowDefinitionService flowDefinitionService, IModuleCatalog moduleCatalog, IRuntimeConfigurationStore store, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var available = moduleCatalog.GetAll();
                var configValues = store.GetState().ConfigValues;
                var response = flowDefinitionService.GetAll()
                    .Select(flow => FlowMappings.MapFlow(flow) with { MissingModules = FlowModuleDependencies.FindMissing(flow, available, configValues) })
                    .ToArray();
                return TypedResults.Ok(response);
            })
            .WithName("GetFlows")
            .WithSummary("List configured flows")
            .WithDescription("Returns the current integration flows loaded from the runtime configuration store, including fetch, parse, orchestration augment, render, and deliver module references.")
            .WithTags("Flows")
            .Produces<FlowResponse[]>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapGet("/api/flows/{flowId}", Results<Ok<FlowResponse>, NotFound> (string flowId, IFlowDefinitionService flowDefinitionService, IModuleCatalog moduleCatalog, IRuntimeConfigurationStore store, CancellationToken cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var flow = flowDefinitionService.GetAll().FirstOrDefault(candidate => string.Equals(candidate.Id, flowId, StringComparison.OrdinalIgnoreCase));
                return flow is null ? TypedResults.NotFound() : TypedResults.Ok(FlowMappings.MapFlow(flow) with
                {
                    MissingModules = FlowModuleDependencies.FindMissing(flow, moduleCatalog.GetAll(), store.GetState().ConfigValues)
                });
            })
            .WithName("GetFlowById")
            .WithSummary("Get a configured flow")
            .WithDescription("Returns a single runtime-configurable flow by id.")
            .WithTags("Flows")
            .Produces<FlowResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        app.MapPost("/api/flows", async Task<Created<FlowResponse>> (CreateFlowRequest request, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                var created = await flowDefinitionService.CreateAsync(FlowMappings.MapPipeline(request), cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"/api/flows/{created.Id}", FlowMappings.MapFlow(created));
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
                var updated = await flowDefinitionService.UpdateAsync(flowId, FlowMappings.MapPipeline(request), cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(FlowMappings.MapFlow(updated));
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

        app.MapPatch("/api/flows/{flowId}/enabled", async Task<Ok<FlowResponse>> (string flowId, SetFlowEnabledRequest request, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                var existing = flowDefinitionService.GetById(flowId);
                var updated = await flowDefinitionService.UpdateAsync(flowId, FlowMappings.WithEnabled(existing, request.Enabled), cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(FlowMappings.MapFlow(updated));
            })
            .WithName("SetFlowEnabled")
            .WithSummary("Enable or disable a flow")
            .WithDescription("Toggles whether a flow is enabled without resending its definition. Imported flows remain blocked until verified translation support exists; reviewing or editing report statuses does not approve execution.")
            .WithTags("Flows")
            .Produces<FlowResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/flows/{flowId}/duplicate", async Task<Created<FlowResponse>> (string flowId, DuplicateFlowRequest request, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                var created = await flowDefinitionService.DuplicateAsync(flowId, request.Id, cancellationToken).ConfigureAwait(false);
                return TypedResults.Created($"/api/flows/{Uri.EscapeDataString(created.Id)}", FlowMappings.MapFlow(created));
            })
            .WithName("DuplicateFlow")
            .WithSummary("Duplicate a flow as a disabled copy")
            .WithTags("Flows")
            .Produces<FlowResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        app.MapPatch("/api/flows/{flowId}/category", async Task<Ok<FlowResponse>> (string flowId, SetFlowCategoryRequest request, IFlowDefinitionService flowDefinitionService, CancellationToken cancellationToken) =>
            {
                var existing = flowDefinitionService.GetById(flowId);
                var updated = await flowDefinitionService.UpdateAsync(flowId, FlowMappings.CopyPipeline(existing, existing.Id, existing.Enabled, request.Category), cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(FlowMappings.MapFlow(updated));
            })
            .WithName("SetFlowCategory")
            .WithSummary("Assign or clear a flow category")
            .WithTags("Flows")
            .Produces<FlowResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

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
                    result.DeliverModules,
                    result.ResponsePayloads?.Count ?? 0,
                    result.Outcome.ToString(),
                    result.NextAttemptAt,
                    ToResponsePayloads(result.ResponsePayloads),
                    result.Detail));
            })
            .WithName("RunFlow")
            .WithSummary("Run a configured flow")
            .WithDescription("Executes a configured flow immediately, regardless of whether it is usually interval-driven. If a delivery route uses a request-response deliver module (for example an HTTP deliver module calling a synchronous endpoint), the captured reply content is included in the response so a caller of this API can act as a two-way (request-response) trigger for the flow.")
            .WithTags("Flows")
            .Produces<FlowRunResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapPost("/api/flows/{flowId}/source-outcomes/retry", async Task<Ok<FlowRunResponse>> (string flowId, IFlowRuntime flowRuntime, CancellationToken cancellationToken) =>
            {
                var result = await flowRuntime.RetrySourceOutcomesAsync(flowId, cancellationToken).ConfigureAwait(false);
                return TypedResults.Ok(new FlowRunResponse(result.FlowId, result.StartedAt, result.CompletedAt,
                    result.PayloadCount, result.DeliverModules, result.ResponsePayloads?.Count ?? 0,
                    result.Outcome.ToString(), result.NextAttemptAt, ToResponsePayloads(result.ResponsePayloads), result.Detail));
            })
            .WithName("RetrySourceOutcomes")
            .WithSummary("Retry failed source notifications without fetching or delivering again")
            .WithDescription("Restarts exhausted source notification attempts using their original receipts/settings. Already acknowledged sources are skipped; the original processing outcome is unchanged.")
            .WithTags("Flows")
            .Produces<FlowRunResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }

    private static IReadOnlyList<FlowRunResponsePayload>? ToResponsePayloads(IReadOnlyList<IntegrationPayload>? payloads)
        => payloads is null || payloads.Count == 0
            ? null
            : payloads.Select(payload => new FlowRunResponsePayload(
                payload.Name,
                payload.ContentType,
                Convert.ToBase64String(payload.Content.ToArray()),
                payload.Metadata)).ToArray();
}
