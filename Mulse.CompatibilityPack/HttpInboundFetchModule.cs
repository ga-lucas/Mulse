using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

/// <summary>
/// Push-triggered fetch module that accepts inbound HTTP requests routed by the host's
/// <see cref="IHttpInboundRegistry"/>. This is the migration target for BizTalk receive locations
/// hosted over WCF-BasicHttp/WSHttp/Custom or a plain HTTP listener: instead of falling back to a
/// repository-style placeholder, the import can point at a real inbound endpoint.
/// </summary>
public sealed class HttpInboundFetchModule(IHttpInboundRegistry registry, ILogger<HttpInboundFetchModule> logger) : IPushTriggeredFetchModule
{
    private static readonly ConcurrentDictionary<string, RouteRegistration> Registrations = new(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("route", "Route", "The inbound route segment this flow listens on, exposed at /api/inbound/{route}.", true),
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Text],
        [ModuleProtocol.Http],
        [ModuleCapability.Ingestion]);

    public ModuleDescriptor Descriptor { get; } = new(
        "http-inbound-fetch",
        "HTTP inbound fetch",
        ModuleKind.Fetch,
        "Accepts pushed HTTP requests on a hosted route, for migrating BizTalk WCF-hosted or HTTP-listener receive locations.",
        SettingDescriptors,
        Recommendation);

    /// <summary><paramref name="input"/> is unused: this is a root source fed by pushed inbound HTTP requests.</summary>
    public Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var route = ResolveRoute(step);
        if (!Registrations.TryGetValue(route, out var registration))
        {
            return Task.FromResult(IntegrationBatch.Empty);
        }

        var payloads = registration.DequeueReady(context);
        return Task.FromResult(new IntegrationBatch(payloads));
    }

    public Task<IAsyncDisposable> RegisterTriggerAsync(
        PushFlowTriggerContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = ResolveRoute(step);
        var registration = Registrations.GetOrAdd(route, key => RouteRegistration.Create(key, registry, logger));
        var subscription = registration.Subscribe(context);
        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    private static string ResolveRoute(ModuleStepDefinition step)
        => ModuleSettings.GetRequired(step.Settings, "route", "http-inbound-fetch").Trim('/');

    private sealed class RouteRegistration : IHttpInboundRoute
    {
        private readonly ConcurrentQueue<HttpInboundRequest> _pending = new();
        private readonly ConcurrentDictionary<Guid, PushFlowTriggerContext> _subscriptions = new();
        private readonly string _routeKey;
        private readonly ILogger _logger;
        private IDisposable? _hostRegistration;

        private RouteRegistration(string routeKey, ILogger logger)
        {
            _routeKey = routeKey;
            _logger = logger;
        }

        public static RouteRegistration Create(string routeKey, IHttpInboundRegistry registry, ILogger logger)
        {
            var registration = new RouteRegistration(routeKey, logger);
            registration._hostRegistration = registry.RegisterRoute(routeKey, registration);
            logger.LogInformation("Registered HTTP inbound route '{Route}' for push-triggered fetch.", routeKey);
            return registration;
        }

        public void Enqueue(HttpInboundRequest request)
        {
            _pending.Enqueue(request);
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Signal($"HTTP {request.Method} request received on route '{_routeKey}'.");
            }
        }

        public IReadOnlyList<IntegrationPayload> DequeueReady(FlowExecutionContext context)
        {
            var payloads = new List<IntegrationPayload>();
            while (_pending.TryDequeue(out var request))
            {
                var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["flowId"] = context.FlowId,
                    ["executionId"] = context.ExecutionId,
                    ["route"] = _routeKey,
                    ["httpMethod"] = request.Method
                };

                foreach (var header in request.Headers)
                {
                    metadata[$"httpHeader.{header.Key}"] = header.Value;
                }

                foreach (var query in request.QueryParameters)
                {
                    metadata[$"httpQuery.{query.Key}"] = query.Value;
                }

                payloads.Add(new IntegrationPayload(
                    $"{_routeKey}-{Guid.CreateVersion7()}",
                    request.Body,
                    string.IsNullOrWhiteSpace(request.ContentType) ? "application/octet-stream" : request.ContentType,
                    metadata));
            }

            return payloads;
        }

        public IAsyncDisposable Subscribe(PushFlowTriggerContext context)
        {
            var subscriptionId = Guid.NewGuid();
            _subscriptions[subscriptionId] = context;
            return new Subscription(this, subscriptionId);
        }

        private ValueTask UnsubscribeAsync(Guid subscriptionId)
        {
            _subscriptions.TryRemove(subscriptionId, out _);
            if (_subscriptions.IsEmpty)
            {
                _hostRegistration?.Dispose();
                Registrations.TryRemove(_routeKey, out _);
                _logger.LogInformation("Removed HTTP inbound route '{Route}'.", _routeKey);
            }

            return ValueTask.CompletedTask;
        }

        private sealed class Subscription(RouteRegistration owner, Guid subscriptionId) : IAsyncDisposable
        {
            private bool _disposed;

            public async ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await owner.UnsubscribeAsync(subscriptionId).ConfigureAwait(false);
            }
        }
    }
}
