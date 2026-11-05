using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.Security;

namespace Service.Tests;

public sealed class DurableBatchSafetyTests
{
    private static ModuleStepDefinition Collector() => new() { Module = "durable-batch-augment", Settings = new()
    {
        ["collectorId"] = "invoices", ["metadataKey"] = "group", ["targetCount"] = "2"
    } };
    private static PipelineDefinition Pipeline() => new()
    {
        Id = "batches", Enabled = true,
        Fetches = [new() { Id = "f", Fetch = new() { Module = "source" } }],
        Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
        Augments = [Collector()],
        Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "destination" } }] }]
    };
    private static FakeModuleCatalog Catalog() => FakeModuleCatalog.WithModules(("source", ModuleKind.Fetch), ("parse", ModuleKind.Parse),
        ("durable-batch-augment", ModuleKind.OrchestrationAugment), ("stateful-orchestration-augment", ModuleKind.OrchestrationAugment),
        ("render", ModuleKind.Render), ("destination", ModuleKind.Deliver));

    [Theory]
    [InlineData("multiple")]
    [InlineData("stateful")]
    [InlineData("conditional")]
    public async Task UnsupportedCollectorPlacementIsRejectedBothAtSaveAndBeforeFetching(string scenario)
    {
        var pipeline = Pipeline();
        if (scenario == "multiple") pipeline.Augments.Add(Collector());
        if (scenario == "stateful") pipeline.Augments.Add(new() { Module = "stateful-orchestration-augment" });
        if (scenario == "conditional") pipeline.Augments[0].Settings["conditionJson"] = "[]";
        var service = new FlowDefinitionService(new FakeRuntimeConfigurationStore(new RuntimeMulseState()), Catalog());
        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, default));
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(pipeline), Catalog(), new InMemoryFlowOrchestrationStateStore(),
            new InMemoryFlowRetryStateStore(), new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState())), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.ExecuteAsync("batches", default));
    }

    [Fact]
    public async Task PendingBatchesBlockProcessingEdits_AtServiceAndAtomicStore_ButCanBeDisabledOrCategorized()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-batch-edit-" + Guid.NewGuid());
        try
        {
            var store = new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
            var original = Pipeline();
            await store.UpsertFlowAsync(original, default);
            await store.UpsertOrchestrationCheckpointAsync(new() { FlowId = "batches", CorrelationKey = "A", Batch = new() { TargetCount = 2 } }, default);
            var service = new FlowDefinitionService(store, Catalog());
            var edited = JsonSerializer.Deserialize<PipelineDefinition>(JsonSerializer.Serialize(original))!;
            edited.Augments[0].Settings["targetCount"] = "3";
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync("batches", edited, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpsertFlowAsync(edited, default));
            var disabled = await service.UpdateAsync("batches", FlowMappings.CopyPipeline(original, original.Id, false, "Pending"), default);
            Assert.False(disabled.Enabled); Assert.Equal("Pending", disabled.Category);
            var restarted = new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
            Assert.Single(restarted.GetState().OrchestrationCheckpoints);
            Assert.Equal("2", restarted.GetState().Pipelines.Single().Augments[0].Settings["targetCount"]);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task FailedCheckpointWriteOrDeleteNeverPublishesUndurableInMemoryState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-batch-write-" + Guid.NewGuid());
        try
        {
            var protector = new FailingProtector();
            RuntimeConfigurationStore New() => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), protector);
            var store = New();
            protector.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => store.UpsertOrchestrationCheckpointAsync(new() { FlowId = "batches", CorrelationKey = "A", Batch = new() }, default));
            Assert.Empty(store.GetState().OrchestrationCheckpoints);
            protector.Fail = false;
            Assert.Empty(New().GetState().OrchestrationCheckpoints);
            await store.UpsertOrchestrationCheckpointAsync(new() { FlowId = "batches", CorrelationKey = "A", Batch = new() }, default);
            protector.Fail = true;
            await Assert.ThrowsAsync<IOException>(() => store.DeleteOrchestrationCheckpointAsync("batches", "A", default));
            Assert.Single(store.GetState().OrchestrationCheckpoints);
            protector.Fail = false;
            Assert.Single(New().GetState().OrchestrationCheckpoints);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task ExactPayloadCountBoundaryIsAccepted_OverflowAndIncomingByteLimitFailBeforePersistence()
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var module = new DurableBatchAugmentModule(store, TimeProvider.System, NullLogger<DurableBatchAugmentModule>.Instance);
        var payload = new IntegrationPayload("a.txt", BinaryData.FromString(""), "text/plain", new Dictionary<string, string> { ["group"] = "A" });
        var step = Collector(); step.Settings["targetCount"] = "10000";
        var atLimit = await module.AugmentAsync(new("batches", "exact", DateTimeOffset.UtcNow), new(Enumerable.Repeat(payload, 10000).ToArray()), step, default);
        Assert.Equal(10000, atLimit.Count); Assert.True(Assert.Single(store.Checkpoints).Batch!.Ready);
        var other = new InMemoryFlowOrchestrationStateStore();
        module = new(other, TimeProvider.System, NullLogger<DurableBatchAugmentModule>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(new("batches", "overflow", DateTimeOffset.UtcNow), new(Enumerable.Repeat(payload, 10001).ToArray()), step, default));
        Assert.Empty(other.Checkpoints);
        var big = payload with { Content = BinaryData.FromBytes(new byte[16000001]) };
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(new("batches", "large", DateTimeOffset.UtcNow), new([big]), step, default));
        Assert.Empty(other.Checkpoints);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldMessagesBlockDeletionReplacementAndReferencedConfigEdits_ButNotUnrelatedConfigOrFlows(bool odx)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-batch-maintenance-" + Guid.NewGuid());
        try
        {
            var store = new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
            var pipeline = Pipeline(); pipeline.Deliveries[0].Render.Settings["fileName"] = "{{config:billing.name}}";
            if (odx) pipeline.Augments[0] = new() { Module = OdxProgramAugmentModule.ModuleId };
            await store.SetConfigValueAsync("billing.name", "invoice.txt", default);
            await store.UpsertFlowAsync(pipeline, default);
            await store.UpsertOrchestrationCheckpointAsync(new() { FlowId = "batches", CorrelationKey = "A", Batch = odx ? null : new(), Odx = odx ? new() : null }, default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFlowAsync("BATCHES", default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteAllFlowsAsync(default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreWorkspaceAsync(new(), replace: true, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetConfigValueAsync("BILLING.NAME", "changed.txt", default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteConfigValueAsync("billing.name", default));
            await store.SetConfigValueAsync("billing.name", "invoice.txt", default);
            await store.SetConfigValueAsync("unrelated", "still editable", default);
            await store.DeleteFlowAsync("unrelated", default);
            await store.RestoreWorkspaceAsync(new() { ConfigValues = new() { ["unrelated-merge"] = "value" } }, replace: false, default);
            Assert.Single(store.GetState().Pipelines);
            Assert.Single(store.GetState().OrchestrationCheckpoints);
            Assert.Equal("invoice.txt", store.GetState().ConfigValues["billing.name"]);
            await store.DeleteOrchestrationCheckpointAsync("batches", "A", default);
            await store.SetConfigValueAsync("billing.name", "changed.txt", default);
            await store.DeleteFlowAsync("batches", default);
            Assert.Empty(store.GetState().Pipelines);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class FailingProtector : IRuntimeStatePayloadProtector
    {
        public bool Fail { get; set; }
        public byte[] Protect(byte[] plaintext) => Fail ? throw new IOException("simulated checkpoint persistence failure") : plaintext;
        public byte[] Unprotect(byte[] protectedPayload) => protectedPayload;
    }
}
