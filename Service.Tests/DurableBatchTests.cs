using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.SamplePlugin;

namespace Service.Tests;

public sealed class DurableBatchTests
{
    private static IntegrationPayload Payload(string group, string text) => new(text + ".txt", BinaryData.FromString(text), "text/plain",
        new Dictionary<string, string> { ["group"] = group });
    private static ModuleStepDefinition Collector(int target = 3) => new() { Module = DurableBatchAugmentModule.ModuleId, Settings = new()
    {
        ["collectorId"] = "billing", ["metadataKey"] = "group", ["targetCount"] = target.ToString(System.Globalization.CultureInfo.InvariantCulture)
    } };
    private static PipelineDefinition Pipeline(int target = 3, RetryPolicyDefinition? retry = null) => new()
    {
        Id = "batches", Enabled = true, Retry = retry,
        Fetches = [new() { Id = "input", Fetch = new() { Module = "source" } }],
        Parses = [new() { Id = "messages", FetchNodeIds = ["input"], Parse = new() { Module = "parse" } }],
        Augments = [Collector(target)],
        Deliveries = [new() { Render = new() { Module = "sample-text-batch-render", Settings = new() { ["separator"] = "|" } },
            Delivers = [new() { Deliver = new() { Module = "destination" } }] }]
    };
    private static RuntimeConfigurationStore Disk(string directory) => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }),
        new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
    private static DurableBatchAugmentModule Module(IFlowOrchestrationStateStore store) => new(store, TimeProvider.System, NullLogger<DurableBatchAugmentModule>.Instance);
    private static FlowRuntime Runtime(PipelineDefinition flow, FakeExecutableModuleCatalog catalog, IFlowOrchestrationStateStore checkpoints, IFlowRetryStateStore retries)
        => new(new FakeFlowDefinitionService(flow), catalog, checkpoints, retries,
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState())), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
    private static FakeExecutableModuleCatalog Catalog(Source source, IFlowOrchestrationStateStore store, List<IntegrationBatch> delivered)
        => new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddAugment(Module(store)).AddRender(new TextBatchRenderModule()).AddDeliver(DelegateModuleFactory.Deliver("destination", delivered));

    [Fact]
    public async Task DiskRestart_CollectsIndependentClientsAcrossRuns_AndAcknowledgesOnlyAfterDelivery()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-durable-batches-" + Guid.NewGuid());
        try
        {
            var source = new Source(new([Payload("A", "a1")]), new([Payload("B", "b1")]),
                new([Payload("A", "a2"), Payload("A", "a3")]), new([Payload("B", "b2"), Payload("B", "b3")]));
            var delivered = new List<IntegrationBatch>();
            var flow = Pipeline();
            async Task<FlowExecutionResult> Run()
            {
                var disk = Disk(directory);
                var checkpoints = new FlowOrchestrationStateStore(disk);
                return await Runtime(flow, Catalog(source, checkpoints, delivered), checkpoints, new FlowRetryStateStore(disk)).ExecuteAsync(flow.Id, default);
            }
            Assert.Equal(FlowExecutionOutcome.Suspended, (await Run()).Outcome);
            Assert.Empty(source.Outcomes); Assert.Empty(delivered);
            var persisted = Assert.Single(Disk(directory).GetState().OrchestrationCheckpoints);
            Assert.Equal("a1", Assert.Single(persisted.Payloads).Restore().GetText());
            Assert.False(persisted.Batch!.Ready);
            Assert.Equal(FlowExecutionOutcome.Suspended, (await Run()).Outcome);
            Assert.Equal(2, Disk(directory).GetState().OrchestrationCheckpoints.Count);
            Assert.Equal(FlowExecutionOutcome.Completed, (await Run()).Outcome);
            Assert.Equal("a1|a2|a3", Assert.Single(Assert.Single(delivered).Payloads).GetText());
            Assert.Equal("3", delivered[0].Payloads[0].Metadata["batch.count"]);
            Assert.Equal(2, source.Outcomes.Count);
            Assert.All(source.Outcomes, outcome => Assert.True(outcome.Succeeded));
            Assert.Equal("B", Assert.Single(Disk(directory).GetState().OrchestrationCheckpoints).CorrelationKey);
            Assert.Equal(FlowExecutionOutcome.Completed, (await Run()).Outcome);
            Assert.Equal("b1|b2|b3", Assert.Single(delivered[1].Payloads).GetText());
            Assert.Equal(4, source.Outcomes.Count);
            Assert.Equal(new[] { 0, 1, 2, 1 }, source.PendingCounts);
            Assert.Empty(Disk(directory).GetState().OrchestrationCheckpoints);
            Assert.Empty(Disk(directory).GetState().PendingRetries);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryRetry_RestartsWithoutRefetchOrRemapping_AndRetainsCheckpointWhileDisabled(bool receipts)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-durable-batch-retry-" + Guid.NewGuid());
        try
        {
            var source = new Source(new([Payload("A", "one")]), new([Payload("A", "two")])) { WithReceipts = receipts };
            var delivered = new List<IntegrationBatch>();
            var flow = Pipeline(2, new() { MaxAttempts = 2, Delay = TimeSpan.Zero });
            var disk = Disk(directory);
            var checkpoints = new FlowOrchestrationStateStore(disk);
            var renderCalls = 0;
            var catalog = Catalog(source, checkpoints, delivered)
                .AddRender(new DelegateRenderModule("sample-text-batch-render", async (context, batch, step, ct) =>
                { renderCalls++; return await new TextBatchRenderModule().RenderAsync(context, batch, step, ct); }))
                .AddDeliver(DelegateModuleFactory.Deliver("destination", delivered, call => call == 1));
            var runtime = Runtime(flow, catalog, checkpoints, new FlowRetryStateStore(disk));
            Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
            Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
            Assert.Empty(source.Outcomes);
            Assert.True(Assert.Single(Disk(directory).GetState().OrchestrationCheckpoints).Batch!.Ready);
            Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
            Assert.Equal(2, source.FetchCount);
            var restart = Disk(directory);
            var state = Assert.Single(restart.GetState().PendingRetries);
            flow = FlowMappings.WithEnabled(flow, false);
            Assert.Null(await Runtime(flow, catalog, checkpoints, new FlowRetryStateStore(restart)).ResumeRetryAsync(state, default));
            Assert.Single(Disk(directory).GetState().PendingRetries);
            flow = FlowMappings.WithEnabled(flow, true);
            var result = await Runtime(flow, catalog, new FlowOrchestrationStateStore(restart), new FlowRetryStateStore(restart)).ResumeRetryAsync(state, default);
            Assert.Equal(FlowExecutionOutcome.Completed, result!.Outcome);
            Assert.Equal(1, renderCalls); Assert.Equal(2, source.FetchCount);
            Assert.Equal("one|two", Assert.Single(Assert.Single(delivered).Payloads).GetText());
            Assert.Equal(receipts ? 2 : 0, source.Outcomes.Count);
            Assert.Empty(Disk(directory).GetState().OrchestrationCheckpoints);
            Assert.Empty(Disk(directory).GetState().PendingRetries);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PermanentIndependentDeliveryFailure_PreservesReadyBatch_AndBlocksNewFetches(bool receipts)
    {
        var source = new Source(new([Payload("A", "one")]), new([Payload("A", "two")])) { WithReceipts = receipts };
        var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var catalog = Catalog(source, checkpoints, []).AddDeliver(DelegateModuleFactory.Deliver("destination", failOnCall: _ => true));
        var runtime = Runtime(Pipeline(2), catalog, checkpoints, new InMemoryFlowRetryStateStore());
        await runtime.ExecuteAsync("batches", default);
        var result = await runtime.ExecuteAsync("batches", default);
        Assert.Contains("destination", result.Detail!);
        Assert.True(Assert.Single(checkpoints.Checkpoints).Batch!.Ready);
        if (receipts) Assert.All(source.Outcomes, outcome => Assert.False(outcome.Succeeded));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync("batches", default));
        Assert.Contains("No new data was fetched", error.Message);
        Assert.Equal(2, source.FetchCount);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task AmbiguousWriteFailure_CollectorStageRetryDoesNotAppendTwice_AndSurvivesDisableWithoutReceipts(int target)
    {
        var inner = new InMemoryFlowOrchestrationStateStore();
        var store = new WriteThenFail(inner);
        var source = new Source(new([Payload("A", "one")]), new([Payload("A", "two")])) { WithReceipts = false };
        var delivered = new List<IntegrationBatch>();
        var retries = new InMemoryFlowRetryStateStore();
        var flow = Pipeline(target, new() { MaxAttempts = 2, Delay = TimeSpan.Zero });
        var catalog = Catalog(source, store, delivered);
        var runtime = Runtime(flow, catalog, store, retries);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync("batches", default)).Outcome);
        Assert.Single(Assert.Single(inner.Checkpoints).Payloads);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync("batches", default)).Outcome);
        Assert.Equal(1, source.FetchCount);
        var pending = Assert.Single(retries.States);
        Assert.Null(pending.CheckpointCorrelationKey);
        Assert.Null(await Runtime(FlowMappings.WithEnabled(flow, false), catalog, store, retries).ResumeRetryAsync(pending, default));
        Assert.Single(retries.States);
        var resumed = (await runtime.ResumeRetryAsync(pending, default))!;
        if (target == 2)
        {
            Assert.Equal(FlowExecutionOutcome.Suspended, resumed.Outcome);
            Assert.Single(Assert.Single(inner.Checkpoints).Payloads);
            Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ExecuteAsync("batches", default)).Outcome);
        }
        else Assert.Equal(FlowExecutionOutcome.Completed, resumed.Outcome);
        Assert.Equal(target == 2 ? "one|two" : "one", Assert.Single(Assert.Single(delivered).Payloads).GetText());
        Assert.Empty(inner.Checkpoints);
    }

    [Fact]
    public async Task AcknowledgementRetryAfterRestart_RetainsReleasedCheckpoint_AndNeverRedelivers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-batch-ack-" + Guid.NewGuid());
        try
        {
            var attempts = 0;
            var source = new Source(new([Payload("A", "one")]), new([Payload("A", "two")]))
            {
                OnOutcome = _ => { if (++attempts == 1) throw new IOException("source temporarily unavailable"); }
            };
            var delivered = new List<IntegrationBatch>();
            var flow = Pipeline(2, new() { MaxAttempts = 2, Delay = TimeSpan.Zero });
            var disk = Disk(directory);
            var checkpoints = new FlowOrchestrationStateStore(disk);
            var catalog = Catalog(source, checkpoints, delivered);
            var runtime = Runtime(flow, catalog, checkpoints, new FlowRetryStateStore(disk));
            Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
            Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
            Assert.Single(delivered);
            Assert.True(Assert.Single(Disk(directory).GetState().OrchestrationCheckpoints).Batch!.Ready);
            var restart = Disk(directory);
            var pending = Assert.Single(restart.GetState().PendingRetries);
            Assert.NotNull(pending.SourceCompletion);
            var result = await Runtime(FlowMappings.WithEnabled(flow, false), catalog, new FlowOrchestrationStateStore(restart), new FlowRetryStateStore(restart)).ResumeRetryAsync(pending, default);
            Assert.Equal(FlowExecutionOutcome.Completed, result!.Outcome);
            Assert.Single(delivered); Assert.Equal(2, source.FetchCount);
            Assert.Equal(3, source.Outcomes.Count);
            Assert.All(source.Outcomes, outcome => Assert.True(outcome.Succeeded));
            Assert.Empty(Disk(directory).GetState().OrchestrationCheckpoints);
            Assert.Empty(Disk(directory).GetState().PendingRetries);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MixedClientsOrEditedCollectorsFailWithoutOverwritingPendingData()
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var module = Module(store);
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(new("batches", "mixed", DateTimeOffset.UtcNow),
            new([Payload("A", "one"), Payload("B", "two")]), Collector(), default));
        Assert.Empty(store.Checkpoints);
        var context = new FlowExecutionContext("batches", "first", DateTimeOffset.UtcNow);
        await module.AugmentAsync(context, new([Payload("A", "one")]), Collector(), default);
        var before = JsonSerializer.Serialize(Assert.Single(store.Checkpoints));
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.AugmentAsync(new("batches", "second", DateTimeOffset.UtcNow),
            new([Payload("A", "two")]), Collector(2), default));
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(store.Checkpoints)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("10001")]
    [InlineData("invalid")]
    public async Task InvalidThresholdsFailExplicitly(string count)
    {
        var step = Collector(); step.Settings["targetCount"] = count;
        var store = new InMemoryFlowOrchestrationStateStore();
        await Assert.ThrowsAsync<ArgumentException>(() => Module(store).AugmentAsync(new("batches", "invalid", DateTimeOffset.UtcNow), new([Payload("A", "one")]), step, default));
        Assert.Empty(store.Checkpoints);
    }

    private sealed class Source(params IntegrationBatch[] batches) : IFetchOutcomeModule
    {
        public bool WithReceipts { get; init; } = true;
        public int FetchCount { get; private set; }
        public List<int> PendingCounts { get; } = [];
        public List<FetchProcessingOutcome> Outcomes { get; } = [];
        public Action<FetchProcessingOutcome>? OnOutcome { get; init; }
        public ModuleDescriptor Descriptor { get; } = new("source", "source", ModuleKind.Fetch, "test");
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken ct)
        {
            PendingCounts.Add(context.PendingSourceReceipts.Count);
            return Task.FromResult(batches[FetchCount++] with { SourceReceipt = WithReceipts ? new Dictionary<string, string> { ["source"] = FetchCount.ToString() } : null });
        }
        public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken ct)
        { Outcomes.Add(outcome); OnOutcome?.Invoke(outcome); return Task.CompletedTask; }
    }
    private sealed class WriteThenFail(IFlowOrchestrationStateStore inner) : IFlowOrchestrationStateStore
    {
        private bool _failed;
        public Task<FlowOrchestrationCheckpoint?> GetAsync(string flow, string key, CancellationToken ct) => inner.GetAsync(flow, key, ct);
        public Task<IReadOnlyList<FlowOrchestrationCheckpoint>> GetAllAsync(string flow, CancellationToken ct) => inner.GetAllAsync(flow, ct);
        public Task<IReadOnlyList<FetchProcessingReceipt>> GetPendingSourceReceiptsAsync(string flow, CancellationToken ct) => inner.GetPendingSourceReceiptsAsync(flow, ct);
        public async Task UpsertAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken ct)
        {
            await inner.UpsertAsync(checkpoint, ct);
            if (!_failed) { _failed = true; throw new IOException("simulated ambiguous write acknowledgement"); }
        }
        public Task DeleteAsync(string flow, string key, CancellationToken ct) => inner.DeleteAsync(flow, key, ct);
    }
}
