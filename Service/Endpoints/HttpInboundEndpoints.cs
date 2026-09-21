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
    public static WebApplication MapHttpInboundEndpoints(this WebApplication app)
    {
        app.MapMethods("/api/inbound/{**route}", ["POST", "PUT"], async Task<Results<Accepted, NotFound<string>>> (
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

                handler.Enqueue(new HttpInboundRequest(
                    request.Method,
                    headers,
                    queryParameters,
                    request.ContentType ?? "application/octet-stream",
                    BinaryData.FromBytes(bodyStream.ToArray())));

                return TypedResults.Accepted((string?)null);
            })
            .WithName("PostHttpInbound")
            .WithSummary("Push an inbound request to a listening flow")
            .WithDescription("Accepts a request for a route registered by an active http-inbound-fetch push trigger and queues it for the next flow execution.")
            .WithTags("Inbound")
            .Produces(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
