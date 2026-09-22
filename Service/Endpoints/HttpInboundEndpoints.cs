using Microsoft.AspNetCore.Http.HttpResults;
using Mulse.Modules;

namespace Service.Endpoints;

/// <summary>
/// Hosts a generic inbound HTTP surface (/api/inbound/{route}) that forwards requests to whichever
/// push-triggered fetch module (e.g. <c>http-inbound-fetch</c>) has registered that route via
/// <see cref="IHttpInboundRegistry"/>. This is the migration target for BizTalk receive locations
/// that were hosted over WCF-BasicHttp/WSHttp/Custom or a plain HTTP listener.
/// </summary>
public static class HttpInboundEndpoints
{
    private const int MaxAwaitResponseSeconds = 120;

    public static WebApplication MapHttpInboundEndpoints(this WebApplication app)
    {
        app.MapMethods("/api/inbound/{**route}", ["POST", "PUT"], async Task<IResult> (
                string route,
                HttpRequest request,
                IHttpInboundRegistry registry,
                CancellationToken cancellationToken) =>
            {
                if (!registry.TryGetRoute(route, out var handler))
                {
                    return TypedResults.NotFound($"No flow is currently listening on inbound route '{route}'.");
                }

                using var bodyStream = new MemoryStream();
                await request.Body.CopyToAsync(bodyStream, cancellationToken).ConfigureAwait(false);

                var headers = request.Headers.ToDictionary(
                    static header => header.Key,
                    static header => header.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);
                var queryParameters = request.Query.ToDictionary(
                    static query => query.Key,
                    static query => query.Value.ToString(),
                    StringComparer.OrdinalIgnoreCase);

                var correlationId = Guid.CreateVersion7().ToString();
                var inboundRequest = new HttpInboundRequest(
                    correlationId,
                    request.Method,
                    headers,
                    queryParameters,
                    request.ContentType ?? "application/octet-stream",
                    BinaryData.FromBytes(bodyStream.ToArray()));

                // BizTalk two-way (request-response) receive locations map to a caller opting into this
                // synchronous mode via ?awaitResponseSeconds=N; one-way receive locations omit it and get
                // the original fire-and-forget 202 behavior, unchanged.
                if (queryParameters.TryGetValue("awaitResponseSeconds", out var awaitRaw)
                    && int.TryParse(awaitRaw, out var awaitSeconds)
                    && awaitSeconds > 0)
                {
                    var timeout = TimeSpan.FromSeconds(Math.Min(awaitSeconds, MaxAwaitResponseSeconds));
                    var reply = await handler.EnqueueAndAwaitReplyAsync(inboundRequest, timeout, cancellationToken).ConfigureAwait(false);
                    if (reply is null)
                    {
                        return TypedResults.Problem(
                            $"No reply was produced for route '{route}' within {timeout.TotalSeconds:0} second(s).",
                            statusCode: StatusCodes.Status504GatewayTimeout);
                    }

                    return new HttpInboundReplyResult(reply);
                }

                handler.Enqueue(inboundRequest);
                return TypedResults.Accepted((string?)null);
            })
            .WithName("PostHttpInbound")
            .WithSummary("Push an inbound request to a listening flow")
            .WithDescription(
                "Accepts a request for a route registered by an active http-inbound-fetch push trigger and queues it for the next flow execution. " +
                "Add ?awaitResponseSeconds=N (up to 120) to hold the connection open and receive a synchronous reply once a paired " +
                "http-inbound-reply-deliver step completes, for migrating BizTalk two-way (request-response) receive locations.")
            .WithTags("Inbound")
            .Produces(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status504GatewayTimeout);

        return app;
    }

    private sealed class HttpInboundReplyResult(HttpInboundReply reply) : IResult
    {
        public Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = reply.StatusCode;
            httpContext.Response.ContentType = reply.ContentType;
            var bytes = reply.Body.ToArray();
            httpContext.Response.ContentLength = bytes.Length;
            return httpContext.Response.Body.WriteAsync(bytes).AsTask();
        }
    }
}

