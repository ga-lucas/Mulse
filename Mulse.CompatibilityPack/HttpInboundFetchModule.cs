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
public sealed class HttpInboundFetchModule(IHttpInboundRegistry registry, ILogger<HttpInboundFetchModule> logger, IDurableInbox? inbox = null) : IPushTriggeredFetchModule, IFetchOutcomeModule
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
    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var route = ResolveRoute(step);
        if (inbox is not null)
        {
            var batch = await inbox.ClaimAsync("http:" + route, context, cancellationToken).ConfigureAwait(false);
            return batch.Count == 0 ? batch : batch with { SourceReceipt = new Dictionary<string, string> { ["channel"] = "http:" + route } };
        }
        if (!Registrations.TryGetValue(route, out var registration)) return IntegrationBatch.Empty;
        return new IntegrationBatch(registration.DequeueReady(context));
    }

    public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken cancellationToken)
        => inbox is null ? Task.CompletedTask : inbox.CompleteAsync(context, receipt.Data["channel"], outcome.Succeeded, cancellationToken);

    public Task<IAsyncDisposable> RegisterTriggerAsync(
        PushFlowTriggerContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var route = ResolveRoute(step);
        var registration = Registrations.GetOrAdd(route, key => RouteRegistration.Create(key, registry, logger, inbox));
        var subscription = registration.Subscribe(context);
        return Task.FromResult<IAsyncDisposable>(subscription);
    }

    private static string ResolveRoute(ModuleStepDefinition step)
        => ModuleSettings.GetRequired(step.Settings, "route", "http-inbound-fetch").Trim('/');

    private sealed class RouteRegistration : IHttpInboundRoute
    {
        private readonly ConcurrentQueue<HttpInboundRequest> _pending = new();
        private readonly ConcurrentDictionary<Guid, PushFlowTriggerContext> _subscriptions = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<HttpInboundReply>> _pendingReplies = new(StringComparer.Ordinal);
        private readonly string _routeKey;
        private readonly ILogger _logger;
        private IDisposable? _hostRegistration;
        private readonly IDurableInbox? _inbox;
        private string? _flowId;

        private RouteRegistration(string routeKey, ILogger logger, IDurableInbox? inbox)
        {
            _routeKey = routeKey;
            _logger = logger;
            _inbox = inbox;
        }

        public static RouteRegistration Create(string routeKey, IHttpInboundRegistry registry, ILogger logger, IDurableInbox? inbox)
        {
            var registration = new RouteRegistration(routeKey, logger, inbox);
            registration._hostRegistration = registry.RegisterRoute(routeKey, registration);
            logger.LogInformation("Registered HTTP inbound route '{Route}' for push-triggered fetch.", routeKey);
            return registration;
        }

        public void Enqueue(HttpInboundRequest request)
        {
            if (_inbox is not null) throw new InvalidOperationException("Durable inbound routes require awaited EnqueueAsync before acknowledging acceptance.");
            _pending.Enqueue(request);
            Signal(request);
        }

        public async Task EnqueueAsync(HttpInboundRequest request, CancellationToken cancellationToken)
        {
            if (_inbox is null) { Enqueue(request); return; }
            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["route"] = _routeKey, ["httpMethod"] = request.Method, ["correlationId"] = request.CorrelationId
            };
            foreach (var header in request.Headers) metadata["httpHeader." + header.Key] = header.Value;
            foreach (var query in request.QueryParameters) metadata["httpQuery." + query.Key] = query.Value;
            await _inbox.EnqueueAsync("http:" + _routeKey, _flowId, Guid.TryParse(request.CorrelationId, out var messageId) ? messageId : Guid.NewGuid(), new IntegrationBatch(
                [new IntegrationPayload(request.CorrelationId, request.Body, request.ContentType, metadata)]), cancellationToken).ConfigureAwait(false);
            Signal(request);
        }

        private void Signal(HttpInboundRequest request)
        {
            foreach (var subscription in _subscriptions.Values)
            {
                subscription.Signal($"HTTP {request.Method} request received on route '{_routeKey}'.");
            }
        }

        public async Task<HttpInboundReply?> EnqueueAndAwaitReplyAsync(HttpInboundRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            var completionSource = new TaskCompletionSource<HttpInboundReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingReplies.TryAdd(request.CorrelationId, completionSource))
            {
                throw new InvalidOperationException($"A reply is already pending for correlation '{request.CorrelationId}'.");
            }

            try
            {
                await EnqueueAsync(request, cancellationToken).ConfigureAwait(false);

                try
                {
                    return await completionSource.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    return null;
                }
            }
            finally
            {
                _pendingReplies.TryRemove(request.CorrelationId, out _);
            }
        }

        public bool TryCompleteReply(string correlationId, HttpInboundReply reply)
        {
            if (_pendingReplies.TryGetValue(correlationId, out var completionSource))
            {
                return completionSource.TrySetResult(reply);
            }

            return false;
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
                    ["httpMethod"] = request.Method,
                    ["correlationId"] = request.CorrelationId
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
            if (_inbox is not null && _flowId is not null && !_flowId.Equals(context.FlowId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Durable HTTP routes must belong to a single flow.");
            _flowId = context.FlowId;
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
