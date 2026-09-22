using Microsoft.Extensions.Logging;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

/// <summary>
/// Deliver module that completes the synchronous reply for an inbound HTTP request previously accepted by
/// <see cref="HttpInboundFetchModule"/>, when the caller used the awaited (two-way) inbound endpoint. Pair this
/// deliver module - on the same "route" setting - with an http-inbound-fetch source to migrate a BizTalk
/// two-way (request-response) receive location: the fetch module admits the request, the flow processes it,
/// and this module relays the rendered result back to the still-waiting caller.
/// </summary>
public sealed class HttpInboundReplyDeliverModule(IHttpInboundRegistry registry, ILogger<HttpInboundReplyDeliverModule> logger) : IDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("route", "Route", "The inbound route this reply targets; must match the paired http-inbound-fetch source's route.", true),
        new("statusCode", "Status code", "HTTP status code to reply to the caller with.", false, ModuleSettingInputKind.Number, "200"),
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Text],
        [ModuleProtocol.Http],
        [ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "http-inbound-reply-deliver",
        "HTTP inbound reply",
        ModuleKind.Deliver,
        "Replies to the original caller of a two-way http-inbound-fetch source with the rendered payload, for migrating BizTalk two-way (request-response) receive locations.",
        SettingDescriptors,
        Recommendation);

    public Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = ModuleSettings.GetRequired(step.Settings, "route", Descriptor.Id).Trim('/');
        var statusCode = ModuleSettings.GetInt32(step.Settings, "statusCode", defaultValue: 200, Descriptor.Id);

        if (!registry.TryGetRoute(route, out var handler))
        {
            logger.LogWarning(
                "No inbound route '{Route}' is currently registered; cannot deliver synchronous reply(ies) for flow {FlowId}.",
                route,
                context.FlowId);
            return Task.CompletedTask;
        }

        foreach (var payload in batch.Payloads)
        {
            if (!payload.Metadata.TryGetValue("correlationId", out var correlationId) || string.IsNullOrWhiteSpace(correlationId))
            {
                logger.LogWarning(
                    "Payload '{Name}' on flow {FlowId} has no correlationId metadata; it cannot be routed back to a waiting inbound caller. " +
                    "Ensure every parse/augment/render step between the http-inbound-fetch source and this deliver module preserves payload metadata.",
                    payload.Name,
                    context.FlowId);
                continue;
            }

            var reply = new HttpInboundReply(statusCode, payload.ContentType, payload.Content);
            if (!handler.TryCompleteReply(correlationId, reply))
            {
                logger.LogInformation(
                    "No caller is currently awaiting correlation '{CorrelationId}' on route '{Route}' (already timed out, or the original request was one-way).",
                    correlationId,
                    route);
            }
        }

        return Task.CompletedTask;
    }
}
