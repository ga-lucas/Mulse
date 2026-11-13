using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules;
using Mulse.Modules.BuiltIn.Parse;
using Mulse.Modules.BuiltIn.Render;
using Service.ConfigValues;
using Service.Execution;

namespace Service.Tests;

public sealed class ReplaySafeStageTests
{
    private static IntegrationBatch Batch(string text = "{\"value\":1}") => new([new("input.json", BinaryData.FromString(text), "application/json", new Dictionary<string, string>())]);
    private static PipelineDefinition Flow() => new()
    {
        Id = "safe", Enabled = true,
        Fetches = [new() { Id = "f", Fetch = new() { Module = "source", Settings = new() { ["receiptPath"] = "original" } } }],
        Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "json-parse" } }],
        Deliveries = [new() { Render = new() { Module = "json-render" }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }]
    };
    private static FlowRuntime Runtime(PipelineDefinition flow, FakeExecutableModuleCatalog catalog, (RuntimeConfigurationStore Store, FlowJournal Journal) pair)
        => new(new FakeFlowDefinitionService(flow), catalog, new FlowOrchestrationStateStore(pair.Store), new FlowRetryStateStore(pair.Store),
            new ConfigValueResolver(pair.Store), TimeProvider.System, NullLogger<FlowRuntime>.Instance, pair.Journal);

    public static IEnumerable<object[]> PureStageCases()
    {
        foreach (var provider in WorkspacePersistenceTests.Providers())
            foreach (var stage in new[] { "parse", "render", "augment" })
                yield return [provider[0], stage];
    }

    [Theory]
    [MemberData(nameof(PureStageCases))]
    public async Task PureStage_PreInvocationContinuationSurvivesRestartWithPinnedState(string provider, string stage)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var source = new Source();
        var pure = new Pure(stage);
        var flow = Flow();
        if (stage == "parse") flow.Parses[0] = new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "pure" } };
        if (stage == "render") flow.Deliveries[0] = new() { Render = new() { Module = "pure" }, Delivers = flow.Deliveries[0].Delivers };
        if (stage == "augment") flow.Augments.Add(new() { Module = "pure" });
        var delivered = 0;
        var catalog = Catalog(source).AddDeliver(new DelegateDeliverModule("deliver", (_, _, _, _) => { delivered++; return Task.CompletedTask; }));
        if (stage == "parse") catalog.AddParse(pure);
        if (stage == "render") catalog.AddRender(pure);
        if (stage == "augment") catalog.AddAugment(pure);
        pure.BeforeInvoke = () =>
        {
            var saved = Assert.Single(pair.Store.GetState().PendingRetries);
            Assert.True(saved.ReplaySafeStage);
            Assert.Single(saved.SourceReceipts);
            Assert.Equal("response", Assert.Single(saved.Responses).Restore().GetText());
            Assert.Equal("original", saved.PipelineSnapshot!.Fetches[0].Fetch.Settings["receiptPath"]);
        };
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ExecuteAsync("safe", default));
        var retry = Assert.Single(pair.Store.GetState().PendingRetries);
        var restarted = fixture.Journal();
        await restarted.Journal.RecoverAsync(default);
        Assert.False(await restarted.Journal.IsDeliveryHeldAsync(retry.ExecutionId, default));
        pure.Interrupt = false;
        pure.BeforeInvoke = null;
        var edited = Flow();
        edited.Fetches[0].Fetch.Settings["receiptPath"] = "changed";
        var result = await Runtime(edited, catalog, restarted).ResumeRetryAsync(retry, default);
        Assert.Equal(FlowExecutionOutcome.Completed, result!.Outcome);
        Assert.Equal("response", Assert.Single(result.ResponsePayloads!).GetText());
        Assert.Equal(1, source.Fetches);
        Assert.Equal(1, source.Completions);
        Assert.Equal("original", source.CompletedReceipt!.Step.Settings["receiptPath"]);
        Assert.Equal(1, delivered);
        Assert.Empty(restarted.Store.GetState().PendingRetries);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task SafeParseThenStartedUnsafeRender_CannotReplayStaleSafeContinuation(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var pure = new Pure("parse");
        var flow = Flow();
        flow.Parses[0] = new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "pure" } };
        flow.Deliveries[0] = new() { Render = new() { Module = "unsafe-render" }, Delivers = flow.Deliveries[0].Delivers };
        var effects = 0;
        var catalog = Catalog(new Source()).AddParse(pure).AddRender(new DelegateRenderModule("unsafe-render", (_, _, _, _) =>
        { effects++; throw new FlowDurabilityException("Process interrupted after external render effect."); })).AddDeliver(DelegateModuleFactory.Deliver("deliver"));
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ExecuteAsync("safe", default));
        var prior = Assert.Single(pair.Store.GetState().PendingRetries);
        pure.Interrupt = false;
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ResumeRetryAsync(prior, default));
        Assert.Equal(1, effects);
        Assert.Empty(pair.Store.GetState().PendingRetries);
        await pair.Store.UpsertRetryStateAsync(prior, default); // Simulate a stale persisted continuation supplied to recovery.
        var restarted = fixture.Journal();
        await restarted.Journal.RecoverAsync(default);
        Assert.True(await restarted.Journal.IsDeliveryHeldAsync(prior.ExecutionId, default));
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, restarted).ResumeRetryAsync(prior, default));
        Assert.Equal(1, effects);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task UnsafeRenderRetry_InterruptedAttemptIsHeldRatherThanRepeated(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var flow = Flow();
        flow.Deliveries[0] = new() { Render = new() { Module = "unsafe-render", Retry = new() { MaxAttempts = 3, Delay = TimeSpan.Zero } }, Delivers = flow.Deliveries[0].Delivers };
        var effects = 0;
        var catalog = Catalog(new Source()).AddRender(new DelegateRenderModule("unsafe-render", (_, _, _, _) =>
        {
            effects++;
            if (effects == 1) throw new InvalidOperationException("Explicit failure permits configured retry.");
            throw new FlowDurabilityException("Interrupted after effect.");
        })).AddDeliver(DelegateModuleFactory.Deliver("deliver"));
        Assert.Equal(FlowExecutionOutcome.Retrying, (await Runtime(flow, catalog, pair).ExecuteAsync("safe", default)).Outcome);
        var retry = Assert.Single(pair.Store.GetState().PendingRetries);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ResumeRetryAsync(retry, default));
        var restarted = fixture.Journal();
        await restarted.Journal.RecoverAsync(default);
        Assert.True(await restarted.Journal.IsDeliveryHeldAsync(retry.ExecutionId, default));
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, restarted).ResumeRetryAsync(retry, default));
        Assert.Equal(2, effects);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task ChangedModuleWithoutMarkerCannotResumePureStage(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var flow = Flow();
        flow.Parses[0] = new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "pure" } };
        var catalog = Catalog(new Source()).AddParse(new Pure("parse")).AddDeliver(DelegateModuleFactory.Deliver("deliver"));
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ExecuteAsync("safe", default));
        var retry = Assert.Single(pair.Store.GetState().PendingRetries);
        var invoked = false;
        catalog.AddParse(new DelegateParseModule("pure", (_, batch, _, _) => { invoked = true; return Task.FromResult(batch); }));
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ResumeRetryAsync(retry, default));
        Assert.False(invoked);
    }

    private static FakeExecutableModuleCatalog Catalog(Source source) => new FakeExecutableModuleCatalog().AddFetch(source).AddParse(new JsonParseModule()).AddRender(new JsonRenderModule());
    private sealed class Source : IFetchOutcomeModule
    {
        public ModuleDescriptor Descriptor { get; } = new("source", "source", ModuleKind.Fetch, "test");
        public int Fetches { get; private set; }
        public int Completions { get; private set; }
        public FetchProcessingReceipt? CompletedReceipt { get; private set; }
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken ct)
        {
            Fetches++;
            context.CaptureResponse(Batch("response"));
            return Task.FromResult(Batch() with { SourceReceipt = new Dictionary<string, string> { ["id"] = "original-receipt" } });
        }
        public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken ct)
        { Completions++; CompletedReceipt = receipt; return Task.CompletedTask; }
    }
    private sealed class Pure(string kind) : IParseModule, IRenderModule, IOrchestrationAugmentModule, IReplaySafeModule
    {
        public ModuleDescriptor Descriptor { get; } = new("pure", "pure", kind == "parse" ? ModuleKind.Parse : kind == "render" ? ModuleKind.Render : ModuleKind.OrchestrationAugment, "test");
        public bool Interrupt { get; set; } = true;
        public Action? BeforeInvoke { get; set; }
        private Task<IntegrationBatch> Invoke(IntegrationBatch batch)
        {
            BeforeInvoke?.Invoke();
            if (Interrupt) throw new FlowDurabilityException("Simulated process interruption.");
            return Task.FromResult(batch);
        }
        public Task<IntegrationBatch> ParseAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken ct) => Invoke(batch);
        public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken ct) => Invoke(batch);
        public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken ct) => Invoke(batch);
    }
}
