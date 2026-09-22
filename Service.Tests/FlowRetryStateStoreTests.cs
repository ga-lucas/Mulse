namespace Service.Tests;

public sealed class FlowRetryStateStoreTests
{
    [Fact]
    public async Task GetAllAsync_NoRetries_ReturnsEmpty()
    {
        var store = new FlowRetryStateStore(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));

        var all = await store.GetAllAsync(CancellationToken.None);

        Assert.Empty(all);
    }

    [Fact]
    public async Task UpsertAsync_NewState_AppearsInGetAllAsync()
    {
        var store = new FlowRetryStateStore(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        var retry = new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0", AttemptsMade = 1 };

        await store.UpsertAsync(retry, CancellationToken.None);
        var all = await store.GetAllAsync(CancellationToken.None);

        var match = Assert.Single(all);
        Assert.Equal("flow-a", match.FlowId);
        Assert.Equal(1, match.AttemptsMade);
    }

    [Fact]
    public async Task UpsertAsync_SameFlowExecutionStage_ReplacesRatherThanDuplicates()
    {
        var store = new FlowRetryStateStore(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        await store.UpsertAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0", AttemptsMade = 1 }, CancellationToken.None);
        await store.UpsertAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0", AttemptsMade = 2 }, CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);

        var match = Assert.Single(all);
        Assert.Equal(2, match.AttemptsMade);
    }

    [Fact]
    public async Task DeleteAsync_RemovesOnlyMatchingStage()
    {
        var store = new FlowRetryStateStore(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        await store.UpsertAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0" }, CancellationToken.None);
        await store.UpsertAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:1" }, CancellationToken.None);

        await store.DeleteAsync("flow-a", "e1", "augment:0", CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);
        var remaining = Assert.Single(all);
        Assert.Equal("augment:1", remaining.StageId);
    }

    [Fact]
    public async Task DeleteAllForExecutionAsync_RemovesEveryStageForThatExecutionOnly()
    {
        var store = new FlowRetryStateStore(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        await store.UpsertAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0" }, CancellationToken.None);
        await store.UpsertAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "delivery:0:render" }, CancellationToken.None);
        await store.UpsertAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e2", StageId = "augment:0" }, CancellationToken.None);

        await store.DeleteAllForExecutionAsync("flow-a", "e1", CancellationToken.None);

        var all = await store.GetAllAsync(CancellationToken.None);
        var remaining = Assert.Single(all);
        Assert.Equal("e2", remaining.ExecutionId);
    }
}
