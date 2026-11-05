using Microsoft.AspNetCore.Http.HttpResults;
using Mulse.Modules.Payloads;
using Service.Models;

namespace Service.Endpoints;

internal static class NativeFlowRecoveryEndpoints
{
    public static WebApplication MapNativeFlowRecoveryEndpoints(this WebApplication app)
    {
        app.MapPost("/api/flows/{flowId}/native/{instanceId}/reconcile", async Task<Ok<FlowRunResponse>>
            (string flowId, string instanceId, ReconcileNativeEffectRequest request, IFlowRuntime runtime, CancellationToken token) =>
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedEffectId);
                ArgumentException.ThrowIfNullOrWhiteSpace(request.Evidence);
                if (request.ExpectedEffectId.Length > 512 || request.Evidence.Length > 4096)
                    throw new ArgumentException("Recovery identity/evidence exceeds its size limit.");
                IntegrationBatch? output = null;
                if (request.Output is not null)
                {
                    if (request.Output.Count > 100) throw new ArgumentException("Recovery accepts at most 100 output payloads.");
                    var payloads = new List<IntegrationPayload>(); long total = 0;
                    foreach (var item in request.Output)
                    {
                        if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 256 || string.IsNullOrWhiteSpace(item.ContentType)
                            || item.ContentType.Length > 256 || item.ContentBase64 is null || item.ContentBase64.Length > 22_369_624
                            || item.Metadata?.Count > 128 || item.Metadata?.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value is null || pair.Value.Length > 4096) == true)
                            throw new ArgumentException("Recovery output requires bounded names, content types, base64 bytes and metadata.");
                        byte[] bytes;
                        try { bytes = Convert.FromBase64String(item.ContentBase64); }
                        catch (FormatException exception) { throw new ArgumentException("Recovery output contains invalid base64 bytes.", exception); }
                        total += bytes.Length;
                        if (total > 16_777_216) throw new ArgumentException("Recovery output exceeds 16 MiB.");
                        payloads.Add(new(item.Name, BinaryData.FromBytes(bytes), item.ContentType, item.Metadata ?? new Dictionary<string, string>()));
                    }
                    output = new(payloads);
                }
                var result = await runtime.ReconcileNativeEffectAsync(flowId, instanceId, request.ExpectedEffectId, request.Evidence, output, token).ConfigureAwait(false);
                return TypedResults.Ok(new FlowRunResponse(result.FlowId, result.StartedAt, result.CompletedAt, result.PayloadCount,
                    result.DeliverModules, result.ResponsePayloads?.Count ?? 0, result.Outcome.ToString(), result.NextAttemptAt,
                    FlowEndpoints.ToResponsePayloads(result.ResponsePayloads), result.Detail));
            })
            .WithName("ReconcileNativeEffect")
            .WithSummary("Record an independently verified committed native effect and resume without replaying it")
            .WithDescription("Requires an enabled flow, exact held effect identity and operator evidence. Output must be supplied when the effect produces payloads. This is not a reset/retry or transaction rollback; subsequent native activities can execute.")
            .WithTags("Flows")
            .ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound).ProducesProblem(StatusCodes.Status409Conflict);
        return app;
    }
}
