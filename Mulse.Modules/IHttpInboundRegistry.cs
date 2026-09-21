namespace Mulse.Modules;

/// <summary>Represents a single inbound HTTP request captured for a push-triggered flow (e.g. a BizTalk WCF-hosted receive location equivalent).</summary>
public sealed record HttpInboundRequest(
    string Method,
    IReadOnlyDictionary<string, string> Headers,
    IReadOnlyDictionary<string, string> QueryParameters,
    string ContentType,
    BinaryData Body);

/// <summary>Implemented by a fetch module registration to accept inbound HTTP requests routed to it by <see cref="IHttpInboundRegistry"/>.</summary>
public interface IHttpInboundRoute
{
    void Enqueue(HttpInboundRequest request);
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
