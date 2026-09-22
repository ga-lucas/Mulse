using Microsoft.Extensions.Logging.Abstractions;
using Mulse.CompatibilityPack;

namespace Service.Tests;

public sealed class HttpInboundFetchModuleTests
{
    private static ModuleStepDefinition CreateStep(string route)
        => new()
        {
            Module = "http-inbound-fetch",
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["route"] = route }
        };

    [Fact]
    public async Task FetchAsync_ReturnsEmptyBatch_WhenNoRequestIsPending()
    {
        var registry = new Service.Triggers.HttpInboundRegistry();
        var module = new HttpInboundFetchModule(registry, NullLogger<HttpInboundFetchModule>.Instance);
        var step = CreateStep("orders-empty-test");
        var context = new FlowExecutionContext("flow-1", "exec-1", DateTimeOffset.UtcNow);

        await using var subscription = await module.RegisterTriggerAsync(
            new PushFlowTriggerContext("flow-1", "http-inbound-fetch", _ => { }),
            step,
            CancellationToken.None);

        var batch = await module.FetchAsync(context, IntegrationBatch.Empty, step, CancellationToken.None);

        Assert.Empty(batch.Payloads);
    }

    [Fact]
    public async Task Enqueue_ThenFetchAsync_ReturnsPayloadWithCorrelationIdMetadata()
    {
        var registry = new Service.Triggers.HttpInboundRegistry();
        var module = new HttpInboundFetchModule(registry, NullLogger<HttpInboundFetchModule>.Instance);
        var step = CreateStep("orders-fetch-test");
        var context = new FlowExecutionContext("flow-1", "exec-1", DateTimeOffset.UtcNow);

        await using var subscription = await module.RegisterTriggerAsync(
            new PushFlowTriggerContext("flow-1", "http-inbound-fetch", _ => { }),
            step,
            CancellationToken.None);

        Assert.True(registry.TryGetRoute("orders-fetch-test", out var route));
        route.Enqueue(new HttpInboundRequest(
            "correlation-1",
            "POST",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            "application/json",
            BinaryData.FromString("{}")));

        var batch = await module.FetchAsync(context, IntegrationBatch.Empty, step, CancellationToken.None);

        var payload = Assert.Single(batch.Payloads);
        Assert.Equal("correlation-1", payload.Metadata["correlationId"]);
    }

    [Fact]
    public async Task EnqueueAndAwaitReplyAsync_ReturnsReply_WhenTryCompleteReplyIsCalled()
    {
        var registry = new Service.Triggers.HttpInboundRegistry();
        var module = new HttpInboundFetchModule(registry, NullLogger<HttpInboundFetchModule>.Instance);
        var step = CreateStep("orders-await-test");

        await using var subscription = await module.RegisterTriggerAsync(
            new PushFlowTriggerContext("flow-1", "http-inbound-fetch", _ => { }),
            step,
            CancellationToken.None);

        Assert.True(registry.TryGetRoute("orders-await-test", out var route));

        var request = new HttpInboundRequest(
            "correlation-2",
            "POST",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            "application/json",
            BinaryData.FromString("{}"));

        var replyTask = route.EnqueueAndAwaitReplyAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);

        var completed = route.TryCompleteReply("correlation-2", new HttpInboundReply(200, "application/json", BinaryData.FromString("{\"ok\":true}")));

        Assert.True(completed);
        var reply = await replyTask;
        Assert.NotNull(reply);
        Assert.Equal(200, reply!.StatusCode);
        Assert.Equal("{\"ok\":true}", reply.Body.ToString());
    }

    [Fact]
    public async Task EnqueueAndAwaitReplyAsync_ReturnsNull_WhenTimeoutElapsesWithoutAReply()
    {
        var registry = new Service.Triggers.HttpInboundRegistry();
        var module = new HttpInboundFetchModule(registry, NullLogger<HttpInboundFetchModule>.Instance);
        var step = CreateStep("orders-timeout-test");

        await using var subscription = await module.RegisterTriggerAsync(
            new PushFlowTriggerContext("flow-1", "http-inbound-fetch", _ => { }),
            step,
            CancellationToken.None);

        Assert.True(registry.TryGetRoute("orders-timeout-test", out var route));

        var request = new HttpInboundRequest(
            "correlation-3",
            "POST",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            "application/json",
            BinaryData.FromString("{}"));

        var reply = await route.EnqueueAndAwaitReplyAsync(request, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.Null(reply);
    }

    [Fact]
    public void TryCompleteReply_ReturnsFalse_WhenNoCallerIsWaiting()
    {
        var registry = new Service.Triggers.HttpInboundRegistry();
        registry.RegisterRoute("orders-no-waiter-test", new PassThroughRoute());

        Assert.True(registry.TryGetRoute("orders-no-waiter-test", out var route));
        var completed = route.TryCompleteReply("unknown-correlation", new HttpInboundReply(200, "application/json", BinaryData.FromString("{}")));

        Assert.False(completed);
    }

    private sealed class PassThroughRoute : IHttpInboundRoute
    {
        public void Enqueue(HttpInboundRequest request)
        {
        }

        public Task<HttpInboundReply?> EnqueueAndAwaitReplyAsync(HttpInboundRequest request, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult<HttpInboundReply?>(null);

        public bool TryCompleteReply(string correlationId, HttpInboundReply reply) => false;
    }
}

public sealed class HttpInboundReplyDeliverModuleTests
{
    private static ModuleStepDefinition CreateStep(string route, string? statusCode = null)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["route"] = route };
        if (statusCode is not null)
        {
            settings["statusCode"] = statusCode;
        }

        return new ModuleStepDefinition { Module = "http-inbound-reply-deliver", Settings = settings };
    }

    [Fact]
    public async Task DeliverAsync_CompletesReply_ForPayloadWithMatchingCorrelationId()
    {
        var registry = new Service.Triggers.HttpInboundRegistry();
        var fetchModule = new HttpInboundFetchModule(registry, NullLogger<HttpInboundFetchModule>.Instance);
        var fetchStep = new ModuleStepDefinition
        {
            Module = "http-inbound-fetch",
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["route"] = "orders-reply-test" }
        };

        await using var subscription = await fetchModule.RegisterTriggerAsync(
            new PushFlowTriggerContext("flow-1", "http-inbound-fetch", _ => { }),
            fetchStep,
            CancellationToken.None);

        Assert.True(registry.TryGetRoute("orders-reply-test", out var route));

        var request = new HttpInboundRequest(
            "correlation-4",
            "POST",
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            "application/json",
            BinaryData.FromString("{}"));
        var replyTask = route.EnqueueAndAwaitReplyAsync(request, TimeSpan.FromSeconds(5), CancellationToken.None);

        var deliverModule = new HttpInboundReplyDeliverModule(registry, NullLogger<HttpInboundReplyDeliverModule>.Instance);
        var deliverStep = CreateStep("orders-reply-test", statusCode: "201");
        var context = new FlowExecutionContext("flow-1", "exec-1", DateTimeOffset.UtcNow);
        var responsePayload = new IntegrationPayload(
            "response",
            BinaryData.FromString("{\"processed\":true}"),
            "application/json",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["correlationId"] = "correlation-4" });

        await deliverModule.DeliverAsync(context, new IntegrationBatch([responsePayload]), deliverStep, CancellationToken.None);

        var reply = await replyTask;
        Assert.NotNull(reply);
        Assert.Equal(201, reply!.StatusCode);
        Assert.Equal("{\"processed\":true}", reply.Body.ToString());
    }

    [Fact]
    public async Task DeliverAsync_DoesNotThrow_WhenPayloadHasNoCorrelationIdMetadata()
    {
        var registry = new Service.Triggers.HttpInboundRegistry();
        var deliverModule = new HttpInboundReplyDeliverModule(registry, NullLogger<HttpInboundReplyDeliverModule>.Instance);
        var step = CreateStep("unregistered-route");
        var context = new FlowExecutionContext("flow-1", "exec-1", DateTimeOffset.UtcNow);
        var payload = new IntegrationPayload("response", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string>());

        await deliverModule.DeliverAsync(context, new IntegrationBatch([payload]), step, CancellationToken.None);
    }
}
