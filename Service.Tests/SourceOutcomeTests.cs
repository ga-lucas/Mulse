using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class SourceOutcomeTests
{
    private static IntegrationBatch Batch() => new([new IntegrationPayload("item.json", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string>())]);
    private static RetryPolicyDefinition Retry(int attempts = 2) => new() { MaxAttempts = attempts, Delay = TimeSpan.Zero };

    private sealed class Source(string id, Action<FetchProcessingReceipt, FetchProcessingOutcome>? callback = null, bool hasReceipt = true) : IFetchOutcomeModule
    {
        public ModuleDescriptor Descriptor { get; } = new(id, id, ModuleKind.Fetch, "test");
        public int FetchCount { get; private set; }
        public List<FetchProcessingOutcome> Outcomes { get; } = [];
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken cancellationToken)
        {
            FetchCount++;
            return Task.FromResult(Batch() with { SourceReceipt = hasReceipt ? new Dictionary<string, string> { ["identity"] = id } : null });
        }
        public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken cancellationToken)
        {
            Outcomes.Add(outcome);
            callback?.Invoke(receipt, outcome);
            return Task.CompletedTask;
        }
    }

    private static PipelineDefinition Pipeline(int delivers = 1, int sources = 1, RetryPolicyDefinition? retry = null, string? atomic = null)
        => new()
        {
            Id = "outcomes", Retry = retry,
            Fetches = Enumerable.Range(0, sources).Select(i => new FetchNodeDefinition
            {
                Id = $"f{i}", Fetch = new ModuleStepDefinition { Module = $"source-{i}", Settings = new() { ["original"] = "saved" } }
            }).ToList(),
            Parses = [new ParseNodeDefinition { Id = "p", FetchNodeIds = Enumerable.Range(0, sources).Select(i => $"f{i}").ToList(), Parse = new ModuleStepDefinition { Module = "parse" } }],
            Deliveries = delivers == 0 ? [] : [new DeliveryRouteDefinition
            {
                AtomicScope = atomic, Render = new ModuleStepDefinition { Module = "render" },
                Delivers = Enumerable.Range(0, delivers).Select(i => new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = $"deliver-{i}" } }).ToList()
            }]
        };

    private static FakeExecutableModuleCatalog Catalog(params Source[] sources)
    {
        var catalog = new FakeExecutableModuleCatalog().AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"));
        foreach (var source in sources) catalog.AddFetch(source);
        return catalog;
    }

    private static FlowRuntime Runtime(PipelineDefinition pipeline, FakeExecutableModuleCatalog catalog, IFlowRetryStateStore retries, IFlowOrchestrationStateStore? checkpoints = null)
        => new(new FakeFlowDefinitionService([pipeline]), catalog, checkpoints ?? new InMemoryFlowOrchestrationStateStore(), retries,
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState())), TimeProvider.System, NullLogger<FlowRuntime>.Instance);

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(50)]
    public async Task Success_NotifiesEverySourceOnlyAfterAllDeliveries(int count)
    {
        var deliveries = 0;
        var sources = Enumerable.Range(0, 2).Select(i => new Source($"source-{i}", (receipt, outcome) =>
        {
            Assert.Equal(count, deliveries);
            Assert.True(outcome.Succeeded);
            Assert.Equal("saved", receipt.Step.Settings["original"]);
        })).ToArray();
        var catalog = Catalog(sources);
        for (var i = 0; i < count; i++) catalog.AddDeliver(new DelegateDeliverModule($"deliver-{i}", (_, _, _, _) => { deliveries++; return Task.CompletedTask; }));
        var store = new InMemoryFlowRetryStateStore();
        var result = await Runtime(Pipeline(count, 2), catalog, store).ExecuteAsync("outcomes", default);
        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        Assert.All(sources, source => Assert.Single(source.Outcomes));
        Assert.Empty(store.States);
    }

    [Theory]
    [InlineData("parse")]
    [InlineData("augment")]
    [InlineData("render")]
    [InlineData("atomic-deliver")]
    public async Task PermanentProcessingFailure_NotifiesFailureAndPreservesOriginalError(string stage)
    {
        var source = new Source("source-0");
        var catalog = Catalog(source).AddDeliver(DelegateModuleFactory.Deliver("deliver-0"));
        var pipeline = Pipeline(atomic: stage == "atomic-deliver" ? "transaction" : null);
        if (stage == "parse") catalog.AddParse(new DelegateParseModule("parse", (_, _, _, _) => throw new InvalidOperationException("original failure")));
        if (stage == "render") catalog.AddRender(new DelegateRenderModule("render", (_, _, _, _) => throw new InvalidOperationException("original failure")));
        if (stage == "augment")
        {
            pipeline.Augments.Add(new ModuleStepDefinition { Module = "augment" });
            catalog.AddAugment(new DelegateAugmentModule("augment", (_, _, _, _) => throw new InvalidOperationException("original failure")));
        }
        if (stage == "atomic-deliver") catalog.AddDeliver(new DelegateDeliverModule("deliver-0", (_, _, _, _) => throw new InvalidOperationException("original failure")));
        var store = new InMemoryFlowRetryStateStore();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Runtime(pipeline, catalog, store).ExecuteAsync("outcomes", default));
        Assert.Equal("original failure", error.Message);
        Assert.False(Assert.Single(source.Outcomes).Succeeded);
        Assert.Empty(store.States);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessingRetry_DefersNotificationUntilSuccessOrExhaustion(bool permanent)
    {
        var source = new Source("source-0");
        var catalog = Catalog(source).AddParse(DelegateModuleFactory.PassthroughParse("parse", call => permanent || call == 1));
        var pipeline = Pipeline(0, retry: Retry());
        var store = new InMemoryFlowRetryStateStore();
        Assert.Equal(FlowExecutionOutcome.Retrying, (await Runtime(pipeline, catalog, store).ExecuteAsync("outcomes", default)).Outcome);
        Assert.Empty(source.Outcomes);
        var state = JsonSerializer.Deserialize<FlowRetryState>(JsonSerializer.Serialize(Assert.Single(store.States)))!;
        Assert.Single(state.SourceReceipts);
        var restarted = Runtime(pipeline, catalog, store);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await restarted.ExecuteAsync("outcomes", default)).Outcome);
        Assert.Equal(1, source.FetchCount);
        if (permanent) await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ResumeRetryAsync(state, default));
        else Assert.Equal(FlowExecutionOutcome.Completed, (await restarted.ResumeRetryAsync(state, default))!.Outcome);
        Assert.Equal(!permanent, Assert.Single(source.Outcomes).Succeeded);
        Assert.Equal(1, source.FetchCount);
        Assert.Empty(store.States);
    }

    [Fact]
    public async Task IndependentFailure_SurvivesLaterDeliveryRetryAndNeverAcknowledgesSuccess()
    {
        var source = new Source("source-0");
        var delivered = new List<IntegrationBatch>();
        var catalog = Catalog(source)
            .AddDeliver(DelegateModuleFactory.Deliver("deliver-0", failOnCall: _ => true))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver-1", delivered, call => call == 1));
        var pipeline = Pipeline(2);
        pipeline.Deliveries[0] = new DeliveryRouteDefinition
        {
            Render = new ModuleStepDefinition { Module = "render" },
            Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "deliver-0" } },
                new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "deliver-1", Retry = Retry() } }]
        };
        var store = new InMemoryFlowRetryStateStore();
        var runtime = Runtime(pipeline, catalog, store);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync("outcomes", default)).Outcome);
        Assert.Empty(source.Outcomes);
        Assert.Single(Assert.Single(store.States).DeliveryFailures);
        var result = await runtime.ResumeRetryAsync(Assert.Single(store.States), default);
        Assert.Equal(FlowExecutionOutcome.Completed, result!.Outcome);
        Assert.Contains("deliver-0", result.Detail!);
        Assert.False(Assert.Single(source.Outcomes).Succeeded);
        Assert.Single(delivered);
    }

    [Fact]
    public async Task CallbackRetry_AfterRealStateRestart_UsesOriginalSettingsAndDoesNotReplayEarlierCallbacksOrDeliveries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-outcomes-" + Guid.NewGuid());
        try
        {
            RuntimeConfigurationStore Store() => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
            var attempts = 0;
            var first = new Source("source-0");
            var second = new Source("source-1", (receipt, _) =>
            {
                Assert.Equal("saved", receipt.Step.Settings["original"]);
                if (++attempts == 1) throw new IOException("cleanup temporarily locked");
            });
            var delivered = new List<IntegrationBatch>();
            var catalog = Catalog(first, second).AddDeliver(DelegateModuleFactory.Deliver("deliver-0", delivered));
            var pipeline = Pipeline(1, 2, Retry());
            var state = Store();
            var result = await Runtime(pipeline, catalog, new FlowRetryStateStore(state)).ExecuteAsync("outcomes", default);
            Assert.Equal(FlowExecutionOutcome.Retrying, result.Outcome);
            var restartedStore = Store();
            var retry = Assert.Single(restartedStore.GetState().PendingRetries);
            Assert.Equal("source-outcome", retry.StageId);
            Assert.Equal(1, retry.SourceReceiptIndex);
            Assert.True(retry.SourceCompletion!.Outcome.Succeeded);
            // Notification must not require the now-edited flow's current modules or configuration.
            var edited = new PipelineDefinition { Id = pipeline.Id, Enabled = false };
            var resumed = await Runtime(edited, catalog, new FlowRetryStateStore(restartedStore)).ResumeRetryAsync(retry, default);
            Assert.Equal(FlowExecutionOutcome.Completed, resumed!.Outcome);
            Assert.Single(first.Outcomes);
            Assert.Equal(2, second.Outcomes.Count);
            Assert.All(second.Outcomes, outcome => Assert.True(outcome.Succeeded));
            Assert.Equal(1, first.FetchCount);
            Assert.Equal(1, second.FetchCount);
            Assert.Single(delivered);
            Assert.Empty(Store().GetState().PendingRetries);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task CallbackExhaustion_NotifiesOtherSourcesAndKeepsOriginalProcessingFailure()
    {
        var first = new Source("source-0", (_, _) => throw new IOException("cleanup failure"));
        var second = new Source("source-1");
        var catalog = Catalog(first, second).AddParse(new DelegateParseModule("parse", (_, _, _, _) => throw new InvalidOperationException("processing failure")));
        var store = new InMemoryFlowRetryStateStore();
        var error = await Assert.ThrowsAsync<AggregateException>(() => Runtime(Pipeline(0, 2), catalog, store).ExecuteAsync("outcomes", default));
        Assert.Contains("processing failure", error.ToString());
        Assert.Contains("cleanup failure", error.ToString());
        Assert.False(Assert.Single(first.Outcomes).Succeeded);
        Assert.False(Assert.Single(second.Outcomes).Succeeded);
        Assert.True(Assert.Single(store.States).SourceCompletion!.NotificationsExhausted);
    }

    [Fact]
    public async Task ExhaustedCleanup_BlocksFreshPollingAndExplicitRecoveryOnlyRetriesFailedSources()
    {
        var attempts = 0;
        var first = new Source("source-0", (_, _) => { if (++attempts == 1) throw new IOException("locked source"); });
        var second = new Source("source-1");
        var delivered = new List<IntegrationBatch>();
        var catalog = Catalog(first, second).AddDeliver(DelegateModuleFactory.Deliver("deliver-0", delivered));
        var store = new InMemoryFlowRetryStateStore();
        var runtime = Runtime(Pipeline(1, 2), catalog, store);
        await Assert.ThrowsAsync<AggregateException>(() => runtime.ExecuteAsync("outcomes", default));
        var pending = Assert.Single(store.States);
        Assert.True(pending.SourceCompletion!.NotificationsExhausted);
        Assert.Single(pending.SourceCompletion.CompletedReceiptIds);
        Assert.Null(await runtime.ResumeRetryAsync(pending, default));
        var blocked = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync("outcomes", default));
        Assert.Contains("prevent resending", blocked.Message);
        Assert.Equal(1, first.FetchCount);
        var recovered = await runtime.RetrySourceOutcomesAsync("outcomes", default);
        Assert.Equal(FlowExecutionOutcome.Completed, recovered.Outcome);
        Assert.Equal(2, first.Outcomes.Count);
        Assert.All(first.Outcomes, outcome => Assert.True(outcome.Succeeded));
        Assert.Single(second.Outcomes);
        Assert.Single(delivered);
        Assert.Empty(store.States);
        Assert.Null(await runtime.ResumeRetryAsync(pending, default));
    }

    [Fact]
    public async Task RecoveryEndpoint_RetriesOnlyNotificationsAndReportsConflictWhenNoneRemain()
    {
        var attempts = 0;
        var source = new Source("source-0", (_, _) => { if (++attempts == 1) throw new IOException("busy"); });
        var catalog = Catalog(source);
        var pipeline = Pipeline(0);
        var retries = new InMemoryFlowRetryStateStore();
        var runtime = Runtime(pipeline, catalog, retries);
        await Assert.ThrowsAsync<AggregateException>(() => runtime.ExecuteAsync("outcomes", default));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddSingleton<IFlowRuntime>(runtime);
        builder.Services.AddSingleton<IModuleCatalog>(catalog);
        builder.Services.AddSingleton<IFlowDefinitionService>(new FakeFlowDefinitionService([pipeline]));
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapFlowEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var recovered = await http.PostAsync("/api/flows/outcomes/source-outcomes/retry", null);
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            using var json = JsonDocument.Parse(await recovered.Content.ReadAsStringAsync());
            Assert.Equal("Completed", json.RootElement.GetProperty("outcome").GetString());
            Assert.Equal(1, source.FetchCount);
            Assert.Equal(2, source.Outcomes.Count);
            Assert.Empty(retries.States);
            using var missing = await http.PostAsync("/api/flows/outcomes/source-outcomes/retry", null);
            Assert.Equal(HttpStatusCode.Conflict, missing.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task CallbackRetryAfterProcessingFailure_KeepsFailureOutcomeAcrossRestart()
    {
        var attempts = 0;
        var source = new Source("source-0", (_, _) => { if (++attempts == 1) throw new IOException("cleanup busy"); });
        var catalog = Catalog(source).AddParse(new DelegateParseModule("parse", (_, _, _, _) => throw new InvalidOperationException("original processing error")));
        var pipeline = Pipeline(0);
        pipeline.Fetches[0] = new FetchNodeDefinition { Id = "f0", Fetch = new ModuleStepDefinition { Module = "source-0", Retry = Retry() } };
        var store = new InMemoryFlowRetryStateStore();
        var runtime = Runtime(pipeline, catalog, store);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync("outcomes", default)).Outcome);
        var pending = JsonSerializer.Deserialize<FlowRetryState>(JsonSerializer.Serialize(Assert.Single(store.States)))!;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ResumeRetryAsync(pending, default));
        Assert.Equal("original processing error", error.Message);
        Assert.Equal(2, source.Outcomes.Count);
        Assert.All(source.Outcomes, outcome => Assert.False(outcome.Succeeded));
        Assert.Equal(1, source.FetchCount);
        Assert.Empty(store.States);
    }

    [Fact]
    public async Task CancelledProcessing_DoesNotReportPermanentFailure()
    {
        var source = new Source("source-0");
        var catalog = Catalog(source).AddParse(new DelegateParseModule("parse", (_, _, _, _) => throw new OperationCanceledException()));
        await Assert.ThrowsAsync<OperationCanceledException>(() => Runtime(Pipeline(0), catalog, new InMemoryFlowRetryStateStore()).ExecuteAsync("outcomes", default));
        Assert.Empty(source.Outcomes);
    }

    [Fact]
    public async Task CancelledCallback_LeavesDurableSuccessIntentAndResumesWithoutFailureCallback()
    {
        var attempts = 0;
        var source = new Source("source-0", (_, _) => { if (++attempts == 1) throw new OperationCanceledException(); });
        var catalog = Catalog(source);
        var store = new InMemoryFlowRetryStateStore();
        var runtime = Runtime(Pipeline(0), catalog, store);
        await Assert.ThrowsAsync<OperationCanceledException>(() => runtime.ExecuteAsync("outcomes", default));
        Assert.True(Assert.Single(store.States).SourceCompletion!.Outcome.Succeeded);
        await runtime.ResumeRetryAsync(Assert.Single(store.States), default);
        Assert.All(source.Outcomes, outcome => Assert.True(outcome.Succeeded));
        Assert.Equal(1, source.FetchCount);
        Assert.Empty(store.States);
    }

    [Fact]
    public async Task OptionalReceipt_IsRequiredForNotification()
    {
        var source = new Source("source-0", hasReceipt: false);
        await Runtime(Pipeline(0), Catalog(source), new InMemoryFlowRetryStateStore()).ExecuteAsync("outcomes", default);
        Assert.Empty(source.Outcomes);
    }

    [Fact]
    public async Task Orchestration_SuspendsReceiptsAndCompletesCheckpointOnlyAfterCallbacksSucceed()
    {
        var attempts = 0;
        var source = new Source("source-0", (_, _) => { if (++attempts == 1) throw new IOException("busy"); });
        var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var catalog = Catalog(source).AddAugment(new StatefulOrchestrationAugmentModule(checkpoints, TimeProvider.System, NullLogger<StatefulOrchestrationAugmentModule>.Instance));
        var pipeline = Pipeline(0, retry: Retry());
        ModuleStepDefinition Orchestrate(string mode) => new()
        {
            Module = "stateful-orchestration-augment", Settings = new()
            {
                ["mode"] = mode, ["bookmark"] = "wait", ["correlationSource"] = "Literal", ["correlationValue"] = "key"
            }
        };
        pipeline.Augments.Add(Orchestrate("Suspend"));
        var retries = new InMemoryFlowRetryStateStore();
        var runtime = Runtime(pipeline, catalog, retries, checkpoints);
        Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync("outcomes", default)).Outcome);
        Assert.Single(Assert.Single(checkpoints.Checkpoints).SourceReceipts);
        Assert.Empty(source.Outcomes);
        pipeline.Augments[0] = Orchestrate("Complete");
        Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync("outcomes", default)).Outcome);
        Assert.Single(checkpoints.Checkpoints);
        Assert.Equal(2, Assert.Single(retries.States).SourceReceipts.Count);
        await runtime.ResumeRetryAsync(Assert.Single(retries.States), default);
        Assert.Empty(checkpoints.Checkpoints);
        Assert.Empty(retries.States);
        Assert.Equal(3, source.Outcomes.Count);
    }
}
