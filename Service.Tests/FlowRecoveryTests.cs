using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Service.Triggers;

namespace Service.Tests;

public sealed class FlowRecoveryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-recovery-" + Guid.NewGuid());

    private RuntimeConfigurationStore Store() => new(Options.Create(new MulseOptions
    {
        RuntimeStatePath = Path.Combine(_directory, "state.json")
    }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);

    private static PipelineDefinition Pipeline() => new()
    {
        Id = "recovery", Enabled = true,
        Retry = new() { MaxAttempts = 3, Delay = TimeSpan.Zero },
        Fetches = [new() { Id = "f", Fetch = new() { Module = "fetch" } }],
        Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
        Deliveries = [new()
        {
            Render = new() { Module = "render" },
            Delivers = [new() { Deliver = new() { Module = "deliver", Settings = new() { ["target"] = "{{secret:target}}" } } }]
        }]
    };

    private static IntegrationBatch Input() => new([new("message", BinaryData.FromString("original"), "text/plain",
        new Dictionary<string, string> { ["group"] = "A", ["messageId"] = "one" })]);

    private static FlowRuntime Runtime(PipelineDefinition flow, FakeExecutableModuleCatalog catalog, RuntimeConfigurationStore store, TimeProvider? clock = null)
        => new(new FakeFlowDefinitionService(flow), catalog, new FlowOrchestrationStateStore(store), new FlowRetryStateStore(store),
            new ConfigValueResolver(store), clock ?? TimeProvider.System, NullLogger<FlowRuntime>.Instance);

    [Fact]
    public async Task ProcessingRetry_RestartsWithOriginalDefinitionSettingsAndPolicy_AcrossFurtherFailures()
    {
        var fetches = 0;
        var renders = 0;
        var attempts = 0;
        var targets = new List<string>();
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("fetch", (_, _, _, _) => { fetches++; return Task.FromResult(Input()); }))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(new DelegateRenderModule("render", (_, batch, _, _) => { renders++; return Task.FromResult(batch); }))
            .AddDeliver(new DelegateDeliverModule("deliver", (_, batch, step, _) =>
            {
                targets.Add(step.Settings["target"]);
                if (++attempts < 3) throw new IOException("Destination temporarily unavailable.");
                delivered.Add(batch);
                return Task.CompletedTask;
            }));
        var flow = Pipeline();
        var store = Store();
        await store.SetConfigValueAsync("target", "original-destination", default);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await Runtime(flow, catalog, store).ExecuteAsync(flow.Id, default)).Outcome);
        var pending = Assert.Single(Store().GetState().PendingRetries);
        Assert.Equal("original-destination", pending.PipelineSnapshot!.Deliveries[0].Delivers[0].Deliver.Settings["target"]);

        flow.Deliveries.Clear();
        flow.Deliveries.Add(new() { Render = new() { Module = "missing-edited-render" }, Delivers = [] });
        flow.Fetches.Clear();
        await store.DeleteConfigValueAsync("target", default);
        var restartedStore = Store();
        var restarted = Runtime(flow, catalog, restartedStore);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await restarted.ResumeRetryAsync(pending, default))!.Outcome);
        pending = Assert.Single(Store().GetState().PendingRetries);
        Assert.Equal(2, pending.AttemptsMade);
        Assert.Equal(3, pending.PipelineSnapshot!.Retry!.MaxAttempts);
        Assert.Equal("render", pending.PipelineSnapshot.Deliveries[0].Render.Module);
        Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(flow, catalog, Store()).ResumeRetryAsync(pending, default))!.Outcome);

        Assert.Equal(new[] { "original-destination", "original-destination", "original-destination" }, targets);
        Assert.Equal("original", Assert.Single(Assert.Single(delivered).Payloads).GetText());
        Assert.Equal(1, fetches);
        Assert.Equal(1, renders);
        Assert.Empty(Store().GetState().PendingRetries);
    }

    [Fact]
    public async Task DisabledProcessingRetry_IsRetainedAcrossRestart_AndResumesWhenEnabled()
    {
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fetch", Input(), call => call == 1))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
        var flow = Pipeline();
        var store = Store();
        await store.SetConfigValueAsync("target", "destination", default);
        await Runtime(flow, catalog, store).ExecuteAsync(flow.Id, default);
        var pending = Assert.Single(Store().GetState().PendingRetries);
        Assert.Empty(pending.SourceReceipts);
        Assert.Null(await Runtime(FlowMappings.WithEnabled(flow, false), catalog, Store()).ResumeRetryAsync(pending, default));
        Assert.Equal(pending.ExecutionId, Assert.Single(Store().GetState().PendingRetries).ExecutionId);
        Assert.Empty(delivered);
        Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(flow, catalog, Store()).ResumeRetryAsync(pending, default))!.Outcome);
        Assert.Single(delivered);
        Assert.Empty(Store().GetState().PendingRetries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnverifiableRetryDefinition_IsRetainedWithoutInvokingStages(bool mismatched)
    {
        var pending = new FlowRetryState
        {
            FlowId = "recovery", ExecutionId = "old", StageId = "fetch:f",
            PipelineSnapshot = mismatched ? new() { Id = "another-flow" } : null
        };
        var store = Store();
        await store.UpsertRetryStateAsync(pending, default);
        var runtime = Runtime(Pipeline(), new FakeExecutableModuleCatalog(), store);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResumeRetryAsync(pending, default));
        Assert.Contains(mismatched ? "does not match" : "legacy processing retry", error.Message);
        Assert.Equal("old", Assert.Single(Store().GetState().PendingRetries).ExecutionId);
    }

    [Fact]
    public async Task NativeWait_IsResumedByDeadlineDriverAfterDiskRestart_WithoutRefetchOrDuplicateDelivery()
    {
        var clock = new Clock();
        var fetches = 0;
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("fetch", (_, _, _, _) => { fetches++; return Task.FromResult(Input()); }))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
        await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        var definition = new NativeFlowDefinition
        {
            Durable = true,
            Nodes = [new() { Id = "wait", Kind = NativeFlowNodeKind.Wait, DurationSeconds = 10 },
                new() { Id = "send", Kind = NativeFlowNodeKind.Deliver, Module = "deliver" }]
        };
        var flow = Pipeline();
        flow.Deliveries.Clear();
        flow.Augments.Add(new() { Module = NativeFlowDefinition.ModuleId,
            Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, JsonSerializerOptions.Web) } });
        FlowRuntime Restart()
        {
            var store = Store();
            catalog.AddAugment(new NativeControlFlowAugmentModule(new AugmentModuleInvoker(provider), new RenderModuleInvoker(provider),
                new FlowOrchestrationStateStore(store), clock, new DeliverModuleInvoker(provider)));
            return Runtime(flow, catalog, store, clock);
        }
        Assert.Equal(FlowExecutionOutcome.Suspended, (await Restart().ExecuteAsync(flow.Id, default)).Outcome);
        var saved = Assert.Single(Store().GetState().OrchestrationCheckpoints);
        Assert.Equal(clock.Now.AddSeconds(10), saved.Native!.DeadlineUtc);
        Assert.Empty(delivered);
        clock.Now = clock.Now.AddSeconds(10);
        var restarted = Restart();
        var observed = new ObservedRuntime(restarted);
        using var driver = new BatchDeadlineFlowService(observed, NullLogger<BatchDeadlineFlowService>.Instance);
        await driver.StartAsync(default);
        try { Assert.Equal(FlowExecutionOutcome.Completed, (await observed.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)))!.Outcome); }
        finally { await driver.StopAsync(default); }
        Assert.Equal(1, fetches);
        Assert.Equal("original", Assert.Single(Assert.Single(delivered).Payloads).GetText());
        Assert.Empty(Store().GetState().OrchestrationCheckpoints);
        Assert.Null(await restarted.ReleaseDueBatchAsync(flow.Id, default));
        Assert.Single(delivered);
    }

    private sealed class ObservedRuntime(IFlowRuntime inner) : IFlowRuntime
    {
        public TaskCompletionSource<FlowExecutionResult?> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<PipelineDefinition> GetConfiguredFlows() => inner.GetConfiguredFlows();
        public async Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flowId, CancellationToken token)
        {
            var result = await inner.ReleaseDueBatchAsync(flowId, token);
            Completed.TrySetResult(result);
            return result;
        }
        public Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken token) => throw new NotSupportedException("Deadline polling must not fetch.");
        public Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flowId, string instanceId, int count, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState state, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
