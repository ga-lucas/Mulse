namespace Service.Tests;

public sealed class FlowOrchestrationStateStoreTests
{
    [Fact]
    public async Task GetAsync_NoMatchingCheckpoint_ReturnsNull()
    {
        var store = new FlowOrchestrationStateStore(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));

        var result = await store.GetAsync("flow-a", "key-1", CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertAsync_ThenGetAsync_ReturnsMatchingCheckpointByFlowAndCorrelationKey()
    {
        var state = new RuntimeMulseState();
        var store = new FlowOrchestrationStateStore(new FakeRuntimeConfigurationStore(state));
        var checkpoint = new FlowOrchestrationCheckpoint { FlowId = "flow-a", CorrelationKey = "key-1", Bookmark = "step-2" };

        await store.UpsertAsync(checkpoint, CancellationToken.None);
        var result = await store.GetAsync("flow-a", "key-1", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("step-2", result!.Bookmark);
    }

    [Fact]
    public async Task GetAsync_IsCaseInsensitiveOnFlowIdAndCorrelationKey()
    {
        var state = new RuntimeMulseState();
        var store = new FlowOrchestrationStateStore(new FakeRuntimeConfigurationStore(state));
        await store.UpsertAsync(new FlowOrchestrationCheckpoint { FlowId = "Flow-A", CorrelationKey = "Key-1" }, CancellationToken.None);

        var result = await store.GetAsync("flow-a", "key-1", CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyMatchingCheckpoint()
    {
        var state = new RuntimeMulseState();
        var store = new FlowOrchestrationStateStore(new FakeRuntimeConfigurationStore(state));
        await store.UpsertAsync(new FlowOrchestrationCheckpoint { FlowId = "flow-a", CorrelationKey = "key-1" }, CancellationToken.None);
        await store.UpsertAsync(new FlowOrchestrationCheckpoint { FlowId = "flow-a", CorrelationKey = "key-2" }, CancellationToken.None);

        await store.DeleteAsync("flow-a", "key-1", CancellationToken.None);

        Assert.Null(await store.GetAsync("flow-a", "key-1", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("flow-a", "key-2", CancellationToken.None));
    }
}
