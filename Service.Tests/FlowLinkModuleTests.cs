namespace Service.Tests;

public sealed class FlowLinkFetchModuleTests
{
    private static ModuleStepDefinition CreateFetchStep(string channel)
        => new()
        {
            Module = "flow-link-fetch",
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["channel"] = channel }
        };

    private static ModuleStepDefinition CreateDeliverStep(string channel)
        => new()
        {
            Module = "flow-link-deliver",
            Settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["channel"] = channel }
        };

    [Fact]
    public async Task FetchAsync_ReturnsEmptyBatch_WhenNothingHasBeenPublished()
    {
        var module = new FlowLinkFetchModule();
        var step = CreateFetchStep("orders-empty-test");
        var context = new FlowExecutionContext("flow-consumer", "exec-1", DateTimeOffset.UtcNow);

        await using var subscription = await module.RegisterTriggerAsync(
            new PushFlowTriggerContext("flow-consumer", "flow-link-fetch", _ => { }),
            step,
            CancellationToken.None);

        var batch = await module.FetchAsync(context, IntegrationBatch.Empty, step, CancellationToken.None);

        Assert.Empty(batch.Payloads);
    }

    [Fact]
    public async Task DeliverAsync_SignalsSubscriber_AndFetchAsync_ReturnsPublishedPayloadWithProvenance()
    {
        var channel = $"orders-{Guid.NewGuid():N}";
        var fetchModule = new FlowLinkFetchModule();
        var fetchStep = CreateFetchStep(channel);

        var signaled = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var subscription = await fetchModule.RegisterTriggerAsync(
            new PushFlowTriggerContext("flow-consumer", "flow-link-fetch", signal => signaled.TrySetResult(signal.Reason)),
            fetchStep,
            CancellationToken.None);

        var deliverModule = new FlowLinkDeliverModule();
        var deliverStep = CreateDeliverStep(channel);
        var producerContext = new FlowExecutionContext("flow-producer", "exec-producer-1", DateTimeOffset.UtcNow);
        var renderedPayload = new IntegrationPayload(
            "order-1.json",
            BinaryData.FromString("{\"orderId\":1}"),
            "application/json",
            new Dictionary<string, string>());

        await deliverModule.DeliverAsync(producerContext, new IntegrationBatch([renderedPayload]), deliverStep, CancellationToken.None);

        var reason = await signaled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(channel, reason);

        var consumerContext = new FlowExecutionContext("flow-consumer", "exec-consumer-1", DateTimeOffset.UtcNow);
        var batch = await fetchModule.FetchAsync(consumerContext, IntegrationBatch.Empty, fetchStep, CancellationToken.None);

        var payload = Assert.Single(batch.Payloads);
        Assert.Equal("order-1.json", payload.Name);
        Assert.Equal("{\"orderId\":1}", payload.Content.ToString());
        Assert.Equal(channel, payload.Metadata["flowLinkChannel"]);
        Assert.Equal("flow-producer", payload.Metadata["sourceFlowId"]);
        Assert.Equal("exec-producer-1", payload.Metadata["sourceExecutionId"]);

        // Drained: a second fetch on the same channel returns nothing further.
        var secondBatch = await fetchModule.FetchAsync(consumerContext, IntegrationBatch.Empty, fetchStep, CancellationToken.None);
        Assert.Empty(secondBatch.Payloads);
    }

    [Fact]
    public async Task DeliverAsync_DoesNotThrow_WhenNoFetchModuleIsSubscribed()
    {
        var deliverModule = new FlowLinkDeliverModule();
        var deliverStep = CreateDeliverStep($"unregistered-{Guid.NewGuid():N}");
        var context = new FlowExecutionContext("flow-producer", "exec-1", DateTimeOffset.UtcNow);
        var payload = new IntegrationPayload("order-1.json", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string>());

        await deliverModule.DeliverAsync(context, new IntegrationBatch([payload]), deliverStep, CancellationToken.None);
    }
}
