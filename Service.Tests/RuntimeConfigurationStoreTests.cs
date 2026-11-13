using Microsoft.Extensions.Options;
using Mulse.Modules.Security;

namespace Service.Tests;

/// <summary>
/// Exercises <see cref="RuntimeConfigurationStore"/> against a real (temp-directory) JSON file, since it's a
/// thin persistence layer whose correctness is mostly about actually reading/writing/round-tripping state -
/// a pure in-memory fake would defeat the point of testing it.
/// </summary>
public sealed class RuntimeConfigurationStoreTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), "mulse-tests-" + Guid.NewGuid());

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private RuntimeConfigurationStore CreateStore(MulseOptions? options = null, IRuntimeStatePayloadProtector? protector = null)
    {
        options ??= new MulseOptions();
        options = new MulseOptions
        {
            RuntimeStatePath = Path.Combine(_tempDirectory, "mulse-runtime.json"),
            PluginDirectories = options.PluginDirectories,
            Pipelines = options.Pipelines
        };
        return new RuntimeConfigurationStore(Options.Create(options), new FakeHostEnvironment(), protector ?? NoOpRuntimeStatePayloadProtector.Instance);
    }

    [Theory]
    [InlineData("add-flow", false)]
    [InlineData("update-flow", false)]
    [InlineData("delete-flow", false)]
    [InlineData("add-retry", false)]
    [InlineData("update-retry", false)]
    [InlineData("delete-stage", false)]
    [InlineData("delete-execution", false)]
    [InlineData("add-flow", true)]
    [InlineData("update-flow", true)]
    [InlineData("delete-flow", true)]
    [InlineData("add-retry", true)]
    [InlineData("update-retry", true)]
    [InlineData("delete-stage", true)]
    [InlineData("delete-execution", true)]
    public async Task FailedMutation_DoesNotPublishOrPersistChanges(string operation, bool cancel)
    {
        var protector = new FailingProtector();
        var store = CreateStore(protector: protector);
        await store.UpsertFlowAsync(new() { Id = "flow-a", Name = "Original" }, default);
        await store.UpsertRetryStateAsync(new() { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0", AttemptsMade = 1 }, default);
        await store.UpsertRetryStateAsync(new() { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:1", AttemptsMade = 1 }, default);
        var original = System.Text.Json.JsonSerializer.Serialize(store.GetState());
        var path = Path.Combine(_tempDirectory, "mulse-runtime.json");
        var originalBytes = await File.ReadAllBytesAsync(path);
        using var cancellation = new CancellationTokenSource();
        protector.OnProtect = () =>
        {
            Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(store.GetState()));
            if (cancel) cancellation.Cancel();
            else throw new IOException("Injected persistence failure.");
        };
        Task Mutate() => operation switch
        {
            "add-flow" => store.UpsertFlowAsync(new() { Id = "flow-b" }, cancellation.Token),
            "update-flow" => store.UpsertFlowAsync(new() { Id = "flow-a", Name = "Changed" }, cancellation.Token),
            "delete-flow" => store.DeleteFlowAsync("flow-a", cancellation.Token),
            "add-retry" => store.UpsertRetryStateAsync(new() { FlowId = "flow-a", ExecutionId = "e2", StageId = "augment:0" }, cancellation.Token),
            "update-retry" => store.UpsertRetryStateAsync(new() { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0", AttemptsMade = 2 }, cancellation.Token),
            "delete-stage" => store.DeleteRetryStateAsync("flow-a", "e1", "augment:0", cancellation.Token),
            "delete-execution" => store.DeleteAllRetryStateForExecutionAsync("flow-a", "e1", cancellation.Token),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(Mutate);
        else await Assert.ThrowsAsync<IOException>(Mutate);
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(store.GetState()));
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(CreateStore().GetState()));
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path));
        Assert.Empty(Directory.GetFiles(_tempDirectory, "*.tmp"));

        protector.OnProtect = null;
        await store.SetConfigValueAsync("after-failure", "saved", default);
        var reloaded = CreateStore().GetState();
        Assert.Equal("saved", reloaded.ConfigValues["after-failure"]);
        reloaded.ConfigValues.Clear();
        Assert.Equal(original, System.Text.Json.JsonSerializer.Serialize(reloaded));
    }

    private sealed class FailingProtector : IRuntimeStatePayloadProtector
    {
        public Action? OnProtect { get; set; }
        public byte[] Protect(byte[] plaintext) { OnProtect?.Invoke(); return plaintext; }
        public byte[] Unprotect(byte[] protectedPayload) => protectedPayload;
    }

    [Fact]
    public void Constructor_NoExistingFile_SeedsStateFromOptionsAndWritesFile()
    {
        var seedPipeline = new PipelineDefinition { Id = "seed-flow" };
        var store = CreateStore(new MulseOptions { Pipelines = [seedPipeline] });

        var state = store.GetState();

        Assert.Single(state.Pipelines);
        Assert.Equal("seed-flow", state.Pipelines[0].Id);
        Assert.True(File.Exists(Path.Combine(_tempDirectory, "mulse-runtime.json")));
    }

    [Fact]
    public async Task UpsertFlowAsync_NewFlow_AddsAndPersistsAcrossNewStoreInstance()
    {
        var options = new MulseOptions { RuntimeStatePath = Path.Combine(_tempDirectory, "mulse-runtime.json") };
        var store = new RuntimeConfigurationStore(Options.Create(options), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);

        await store.UpsertFlowAsync(new PipelineDefinition { Id = "flow-a" }, CancellationToken.None);

        var reloaded = new RuntimeConfigurationStore(Options.Create(options), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
        Assert.Contains(reloaded.GetState().Pipelines, pipeline => pipeline.Id == "flow-a");
    }

    [Fact]
    public async Task UpsertFlowAsync_ExistingFlow_ReplacesRatherThanDuplicates()
    {
        var store = CreateStore();
        await store.UpsertFlowAsync(new PipelineDefinition { Id = "flow-a", Enabled = true }, CancellationToken.None);
        await store.UpsertFlowAsync(new PipelineDefinition { Id = "flow-a", Enabled = false }, CancellationToken.None);

        var state = store.GetState();
        var match = Assert.Single(state.Pipelines, pipeline => pipeline.Id == "flow-a");
        Assert.False(match.Enabled);
    }

    [Fact]
    public async Task DeleteFlowAsync_RemovesFlowAndItsOrchestrationAndRetryState()
    {
        var store = CreateStore();
        await store.UpsertFlowAsync(new PipelineDefinition { Id = "flow-a" }, CancellationToken.None);
        await store.UpsertOrchestrationCheckpointAsync(new FlowOrchestrationCheckpoint { FlowId = "flow-a", CorrelationKey = "k1" }, CancellationToken.None);
        await store.UpsertRetryStateAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0" }, CancellationToken.None);

        await store.DeleteFlowAsync("flow-a", CancellationToken.None);

        var state = store.GetState();
        Assert.DoesNotContain(state.Pipelines, pipeline => pipeline.Id == "flow-a");
        Assert.DoesNotContain(state.OrchestrationCheckpoints, checkpoint => checkpoint.FlowId == "flow-a");
        Assert.DoesNotContain(state.PendingRetries, retry => retry.FlowId == "flow-a");
    }

    [Fact]
    public async Task UpsertRetryStateAsync_SameStage_ReplacesRatherThanDuplicates()
    {
        var store = CreateStore();
        await store.UpsertRetryStateAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0", AttemptsMade = 1 }, CancellationToken.None);
        await store.UpsertRetryStateAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0", AttemptsMade = 2 }, CancellationToken.None);

        var state = store.GetState();
        var match = Assert.Single(state.PendingRetries);
        Assert.Equal(2, match.AttemptsMade);
    }

    [Fact]
    public async Task DeleteAllRetryStateForExecutionAsync_RemovesOnlyMatchingExecution()
    {
        var store = CreateStore();
        await store.UpsertRetryStateAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e1", StageId = "augment:0" }, CancellationToken.None);
        await store.UpsertRetryStateAsync(new FlowRetryState { FlowId = "flow-a", ExecutionId = "e2", StageId = "augment:0" }, CancellationToken.None);

        await store.DeleteAllRetryStateForExecutionAsync("flow-a", "e1", CancellationToken.None);

        var state = store.GetState();
        var remaining = Assert.Single(state.PendingRetries);
        Assert.Equal("e2", remaining.ExecutionId);
    }

    [Fact]
    public async Task SetConfigValueAsync_ThenDeleteConfigValueAsync_RoundTrips()
    {
        var store = CreateStore();
        await store.SetConfigValueAsync("ftp-host", "sftp.example.com", CancellationToken.None);
        Assert.Equal("sftp.example.com", store.GetState().ConfigValues["ftp-host"]);

        await store.DeleteConfigValueAsync("ftp-host", CancellationToken.None);
        Assert.False(store.GetState().ConfigValues.ContainsKey("ftp-host"));
    }

    [Fact]
    public void GetState_ReturnsIndependentCopy_MutatingItDoesNotAffectStore()
    {
        var store = CreateStore(new MulseOptions { Pipelines = [new PipelineDefinition { Id = "flow-a" }] });

        var firstCopy = store.GetState();
        firstCopy.Pipelines.Clear();

        Assert.Single(store.GetState().Pipelines);
    }
}
