using System.Collections.Concurrent;
using Mulse.Modules;

namespace Service;

/// <summary>
/// Default <see cref="IHttpInboundRegistry"/> implementation: an in-memory, thread-safe map from
/// route key to the currently active push-fetch registration. The ASP.NET Core hosting layer
/// (see <see cref="Endpoints.HttpInboundEndpoints"/>) looks routes up here so it never needs a
/// direct reference to the module implementation that registered them.
/// </summary>
public sealed class HttpInboundRegistry : IHttpInboundRegistry
{
    private readonly ConcurrentDictionary<string, IHttpInboundRoute> _routes = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetRoute(string routeKey, out IHttpInboundRoute route)
        => _routes.TryGetValue(routeKey, out route!);

    public IDisposable RegisterRoute(string routeKey, IHttpInboundRoute route)
    {
        _routes[routeKey] = route;
        return new Registration(this, routeKey);
    }

    private sealed class Registration(HttpInboundRegistry owner, string routeKey) : IDisposable
    {
        public void Dispose() => owner._routes.TryRemove(routeKey, out _);
    }
}
