namespace Mulse.Modules.Triggers;

/// <summary>Represents a single inbound HTTP request captured for a push-triggered flow (e.g. a BizTalk WCF-hosted receive location equivalent).</summary>
public sealed record HttpInboundRequest(
    string CorrelationId,
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> QueryParameters,
    string ContentType,
    BinaryData Body);

/// <summary>
/// The synchronous reply captured for a two-way (request-response) inbound HTTP request, produced by a
/// deliver module further down the same route (see <c>HttpInboundReplyDeliverModule</c>) and relayed back
/// to whichever caller is still awaiting <see cref="IHttpInboundRoute.EnqueueAndAwaitReplyAsync"/>.
/// </summary>
public sealed record HttpInboundReply(int StatusCode, string ContentType, BinaryData Body);

/// <summary>Implemented by a fetch module registration to accept inbound HTTP requests routed to it by <see cref="IHttpInboundRegistry"/>.</summary>
public interface IHttpInboundRoute
{
    /// <summary>Queues the request for the next flow execution without waiting for a reply (BizTalk one-way receive location equivalent).</summary>
    void Enqueue(HttpInboundRequest request);

    /// <summary>
    /// Queues the request like <see cref="Enqueue"/>, but also waits up to <paramref name="timeout"/> for a
    /// deliver module to complete the reply for <see cref="HttpInboundRequest.CorrelationId"/> via
    /// <see cref="TryCompleteReply"/>. Enables BizTalk two-way (request-response) receive location migration.
    /// Returns <see langword="null"/> if no reply arrives before the timeout elapses.
    /// </summary>
    Task<HttpInboundReply?> EnqueueAndAwaitReplyAsync(HttpInboundRequest request, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Completes the pending reply for <paramref name="correlationId"/>, if a caller is still awaiting it. Returns <see langword="false"/> otherwise (e.g. already timed out, or a one-way request).</summary>
    bool TryCompleteReply(string correlationId, HttpInboundReply reply);
}

/// <summary>
/// Decouples the ASP.NET Core hosting layer (which owns the actual HTTP endpoint) from the module
/// authoring layer (which has no ASP.NET Core dependency). A fetch module registers a route key here
/// when its flow starts listening for push triggers; the hosting layer looks the route up when a
/// request arrives and forwards it, without either side needing a direct reference to the other.
/// </summary>
public interface IHttpInboundRegistry
{
    /// <summary>Attempts to resolve the currently active route registration for the given route key (case-insensitive).</summary>
    bool TryGetRoute(string routeKey, out IHttpInboundRoute route);

    /// <summary>Registers a route handler for the given route key. Disposing the result unregisters it.</summary>
    IDisposable RegisterRoute(string routeKey, IHttpInboundRoute route);
}
