using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.SamplePlugin;
using Service.Triggers;

namespace Service.Tests;

public sealed class TimedDurableBatchTests
{
    private static ModuleStepDefinition Collector(int target = 10) => new() { Module = "durable-batch-augment", Settings = new()
    {
        ["collectorId"] = "invoices", ["metadataKey"] = "group", ["targetCount"] = target.ToString(CultureInfo.InvariantCulture),
        ["releaseMode"] = "CountOrDailyCutoff", ["dailyCutoff"] = "10:15", ["timeZoneId"] = "UTC"
    } };
    private static PipelineDefinition Pipeline(int target = 10, RetryPolicyDefinition? retry = null) => new()
    {
        Id = "timed", Enabled = true, Retry = retry,
        Fetches = [new() { Id = "f", Fetch = new() { Module = "source" } }],
        Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
        Augments = [Collector(target)],
        Deliveries = [new() { Render = new() { Module = "sample-text-batch-render", Settings = new() { ["separator"] = "|" } },
            Delivers = [new() { Deliver = new() { Module = "destination" } }] }]
    };
    private static IntegrationPayload Payload(string text, string group = "A") => new(text, BinaryData.FromString(text), "text/plain", new Dictionary<string, string> { ["group"] = group });
    private static FlowRuntime Runtime(PipelineDefinition pipeline, FakeExecutableModuleCatalog catalog, IFlowOrchestrationStateStore checkpoints, IFlowRetryStateStore retries, TimeProvider clock)
        => new(new FakeFlowDefinitionService(pipeline), catalog, checkpoints, retries,
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), clock, NullLogger<FlowRuntime>.Instance);
    private static FakeExecutableModuleCatalog Catalog(Source source, IFlowOrchestrationStateStore store, TimeProvider clock, List<IntegrationBatch> delivered)
        => new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddAugment(new DurableBatchAugmentModule(store, clock, NullLogger<DurableBatchAugmentModule>.Instance))
            .AddRender(new TextBatchRenderModule()).AddDeliver(DelegateModuleFactory.Deliver("destination", delivered));
    private static RuntimeConfigurationStore Disk(string directory) => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);

    [Theory]
    [InlineData("UTC", "18:00", "2026-04-01T17:59:59Z", "2026-04-01T18:00:00Z")]
    [InlineData("UTC", "18:00", "2026-04-01T18:00:00Z", "2026-04-02T18:00:00Z")]
    [InlineData("W. Europe Standard Time", "02:30", "2026-03-29T00:15:00Z", "2026-03-29T01:00:00Z")]
    [InlineData("W. Europe Standard Time", "02:30", "2026-10-25T00:45:00Z", "2026-10-25T01:30:00Z")]
    [InlineData("W. Europe Standard Time", "02:30", "2026-10-25T01:30:00Z", "2026-10-26T01:30:00Z")]
    public void DailyClockHasExplicitStrictFutureAndDstSemantics(string zone, string cutoff, string now, string expected)
    {
        var step = Collector(); step.Settings["timeZoneId"] = zone; step.Settings["dailyCutoff"] = cutoff;
        Assert.Equal(DateTimeOffset.Parse(expected, CultureInfo.InvariantCulture), DurableBatchSchedule.Read(step)!.NextDeadline(DateTimeOffset.Parse(now, CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void ScheduleIdentityIsCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            var schedule = DurableBatchSchedule.Read(Collector())!;
            CultureInfo.CurrentCulture = new CultureInfo("fi-FI");
            Assert.Equal("Daily|10:15|UTC", schedule.Key);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData("timeZoneId", "")]
    [InlineData("timeZoneId", "Not/A/Timezone")]
    [InlineData("dailyCutoff", "24:00")]
    [InlineData("dailyCutoff", "10:15:30")]
    [InlineData("releaseMode", "unknown")]
    public async Task InvalidClockCannotBeEnabledOrFetchInputs(string setting, string value)
    {
        var clock = new Clock(); var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var source = new Source(); var catalog = Catalog(source, checkpoints, clock, []); var flow = Pipeline();
        flow.Augments[0].Settings[setting] = value;
        var definitions = new FlowDefinitionService(new FakeRuntimeConfigurationStore(new()), catalog);
        await Assert.ThrowsAsync<ArgumentException>(() => definitions.CreateAsync(flow, default));
        await definitions.CreateAsync(FlowMappings.WithEnabled(flow, false), default);
        await Assert.ThrowsAsync<ArgumentException>(() => Runtime(flow, catalog, checkpoints, new InMemoryFlowRetryStateStore(), clock).ExecuteAsync(flow.Id, default));
        Assert.Equal(0, source.Fetches); Assert.Empty(checkpoints.Checkpoints);
    }

    [Fact]
    public async Task DueClientsReleaseAfterDiskRestartWithoutFetching_DeadlinesDoNotSlideWithArrivals()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-timed-batch-" + Guid.NewGuid());
        try
        {
            var clock = new Clock(); var source = new Source(new([Payload("a1")]), new([Payload("b1", "B")]), new([Payload("a2")]));
            var delivered = new List<IntegrationBatch>(); var flow = Pipeline();
            FlowRuntime Restart()
            {
                var disk = Disk(directory); var checkpoints = new FlowOrchestrationStateStore(disk);
                return Runtime(flow, Catalog(source, checkpoints, clock, delivered), checkpoints, new FlowRetryStateStore(disk), clock);
            }
            Assert.Equal(FlowExecutionOutcome.Suspended, (await Restart().ExecuteAsync(flow.Id, default)).Outcome);
            var deadline = Assert.Single(Disk(directory).GetState().OrchestrationCheckpoints).Batch!.DeadlineUtc;
            Assert.Equal(DateTimeOffset.Parse("2026-04-01T10:15:00Z", CultureInfo.InvariantCulture), deadline);
            clock.Now = clock.Now.AddMinutes(10);
            await Restart().ExecuteAsync(flow.Id, default);
            clock.Now = clock.Now.AddMinutes(10);
            await Restart().ExecuteAsync(flow.Id, default);
            Assert.Equal(deadline, Disk(directory).GetState().OrchestrationCheckpoints.Single(c => c.CorrelationKey == "A").Batch!.DeadlineUtc);
            Assert.Null(await Restart().ReleaseDueBatchAsync(flow.Id, default)); Assert.Empty(delivered); Assert.Empty(source.Outcomes);
            clock.Now = deadline!.Value;
            Assert.Equal(FlowExecutionOutcome.Completed, (await Restart().ExecuteAsync(flow.Id, default)).Outcome);
            Assert.Equal("a1|a2", Assert.Single(delivered[0].Payloads).GetText());
            Assert.Equal(FlowExecutionOutcome.Completed, (await Restart().ReleaseDueBatchAsync(flow.Id, default))!.Outcome);
            Assert.Equal("b1", Assert.Single(delivered[1].Payloads).GetText());
            Assert.Equal(3, source.Fetches); Assert.Equal(3, source.Outcomes.Count); Assert.All(source.Outcomes, outcome => Assert.True(outcome.Succeeded));
            Assert.Empty(Disk(directory).GetState().OrchestrationCheckpoints);
            Assert.Null(await Restart().ReleaseDueBatchAsync(flow.Id, default)); Assert.Equal(2, delivered.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task DeadlineDeliveryRetriesNeverRefetch_AndPendingRetriesBlockDeadlinePolls()
    {
        var clock = new Clock(); var source = new Source(new IntegrationBatch([Payload("one")])); var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var retries = new InMemoryFlowRetryStateStore(); var delivered = new List<IntegrationBatch>();
        var flow = Pipeline(retry: new() { MaxAttempts = 2, Delay = TimeSpan.Zero });
        var catalog = Catalog(source, checkpoints, clock, delivered).AddDeliver(DelegateModuleFactory.Deliver("destination", delivered, call => call == 1));
        var runtime = Runtime(flow, catalog, checkpoints, retries, clock);
        await runtime.ExecuteAsync(flow.Id, default); clock.Now = Assert.Single(checkpoints.Checkpoints).Batch!.DeadlineUtc!.Value;
        Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ReleaseDueBatchAsync(flow.Id, default))!.Outcome);
        Assert.True(Assert.Single(checkpoints.Checkpoints).Batch!.Ready);
        Assert.Null(await runtime.ReleaseDueBatchAsync(flow.Id, default)); Assert.Equal(1, source.Fetches);
        Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ResumeRetryAsync(Assert.Single(retries.States), default))!.Outcome);
        Assert.Single(delivered); Assert.Single(source.Outcomes); Assert.Empty(checkpoints.Checkpoints);
        Assert.Null(await runtime.ReleaseDueBatchAsync(flow.Id, default));
    }

    [Fact]
    public async Task IdleManualFlowIsReleasedByHostedDriver_AndDisabledFlowRetainsItsDeadline()
    {
        var clock = new Clock(); var source = new Source(new IntegrationBatch([Payload("one")])); var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var delivered = new List<IntegrationBatch>(); var flow = Pipeline(); var catalog = Catalog(source, checkpoints, clock, delivered);
        var runtime = Runtime(flow, catalog, checkpoints, new InMemoryFlowRetryStateStore(), clock);
        await runtime.ExecuteAsync(flow.Id, default); clock.Now = Assert.Single(checkpoints.Checkpoints).Batch!.DeadlineUtc!.Value;
        var disabled = Runtime(FlowMappings.WithEnabled(flow, false), catalog, checkpoints, new InMemoryFlowRetryStateStore(), clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disabled.ReleaseDueBatchAsync(flow.Id, default));
        Assert.Empty(delivered); Assert.Single(checkpoints.Checkpoints);
        var observed = new ObservedRuntime(runtime);
        using var driver = new BatchDeadlineFlowService(observed, NullLogger<BatchDeadlineFlowService>.Instance);
        await driver.StartAsync(default);
        try { Assert.Equal(FlowExecutionOutcome.Completed, (await observed.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)))!.Outcome); }
        finally { await driver.StopAsync(default); }
        Assert.Single(delivered); Assert.Empty(checkpoints.Checkpoints); Assert.Equal(1, source.Fetches);
        var competing = await Task.WhenAll(runtime.ReleaseDueBatchAsync(flow.Id, default), runtime.ReleaseDueBatchAsync(flow.Id, default));
        Assert.All(competing, Assert.Null); Assert.Single(delivered);
    }

    [Fact]
    public async Task CountThresholdStillReleasesBeforeDeadline_WithoutLeavingAnEmptyTimedBatch()
    {
        var clock = new Clock(); var source = new Source(new IntegrationBatch([Payload("one")])); var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var delivered = new List<IntegrationBatch>(); var runtime = Runtime(Pipeline(1), Catalog(source, checkpoints, clock, delivered), checkpoints, new InMemoryFlowRetryStateStore(), clock);
        Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ExecuteAsync("timed", default)).Outcome);
        clock.Now = clock.Now.AddDays(1);
        Assert.Null(await runtime.ReleaseDueBatchAsync("timed", default)); Assert.Single(delivered); Assert.Empty(checkpoints.Checkpoints);
    }

    [Fact]
    public async Task ConcurrentEligibleDeadlinePollsDeliverOnce_AndReadyFailuresNeverAutoReplay()
    {
        var clock = new Clock(); var source = new Source(new IntegrationBatch([Payload("one")])); var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var delivered = new List<IntegrationBatch>(); var flow = Pipeline(); var catalog = Catalog(source, checkpoints, clock, delivered);
        var runtime = Runtime(flow, catalog, checkpoints, new InMemoryFlowRetryStateStore(), clock);
        await runtime.ExecuteAsync(flow.Id, default); clock.Now = Assert.Single(checkpoints.Checkpoints).Batch!.DeadlineUtc!.Value;
        var results = await Task.WhenAll(runtime.ReleaseDueBatchAsync(flow.Id, default), runtime.ReleaseDueBatchAsync(flow.Id, default));
        Assert.Single(results, result => result is not null); Assert.Single(delivered); Assert.Equal(1, source.Fetches);
        source = new Source(new IntegrationBatch([Payload("two")]));
        catalog = Catalog(source, checkpoints, clock, delivered).AddDeliver(DelegateModuleFactory.Deliver("destination", failOnCall: _ => true));
        runtime = Runtime(flow, catalog, checkpoints, new InMemoryFlowRetryStateStore(), clock);
        await runtime.ExecuteAsync(flow.Id, default); clock.Now = Assert.Single(checkpoints.Checkpoints).Batch!.DeadlineUtc!.Value;
        var failed = await runtime.ReleaseDueBatchAsync(flow.Id, default);
        Assert.Contains("destination", failed!.Detail!); Assert.True(Assert.Single(checkpoints.Checkpoints).Batch!.Ready);
        Assert.Null(await runtime.ReleaseDueBatchAsync(flow.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(flow.Id, default));
        Assert.Equal(1, source.Fetches); Assert.Single(delivered);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-04-01T09:10:00Z", CultureInfo.InvariantCulture);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Source(params IntegrationBatch[] inputs) : IFetchOutcomeModule
    {
        public int Fetches { get; private set; }
        public List<FetchProcessingOutcome> Outcomes { get; } = [];
        public ModuleDescriptor Descriptor { get; } = new("source", "source", ModuleKind.Fetch, "test");
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken ct)
            => Task.FromResult(inputs[Fetches++] with { SourceReceipt = new Dictionary<string, string> { ["source"] = Fetches.ToString(CultureInfo.InvariantCulture) } });
        public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken ct)
        { Outcomes.Add(outcome); return Task.CompletedTask; }
    }
    private sealed class ObservedRuntime(IFlowRuntime inner) : IFlowRuntime
    {
        public TaskCompletionSource<FlowExecutionResult?> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<PipelineDefinition> GetConfiguredFlows() => inner.GetConfiguredFlows();
        public async Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flow, CancellationToken ct)
        {
            var result = await inner.ReleaseDueBatchAsync(flow, ct); Completed.TrySetResult(result); return result;
        }
        public Task<FlowExecutionResult> ExecuteAsync(string flow, CancellationToken ct) => throw new NotSupportedException("Deadline polling must not fetch.");
        public Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flow, string instance, int count, CancellationToken ct) => throw new NotSupportedException("Deadline polling must not force release.");
        public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState state, CancellationToken ct) => throw new NotSupportedException();
        public Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flow, CancellationToken ct) => throw new NotSupportedException();
    }
}
