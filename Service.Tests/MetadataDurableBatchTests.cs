using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Mulse.CompatibilityPack;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Decisions;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class MetadataDurableBatchTests
{
    private static ModuleStepDefinition Collector() => new() { Module = DurableBatchAugmentModule.ModuleId, Settings = new()
    {
        ["collectorId"] = "billing", ["metadataKey"] = "rules.GroupID", ["targetCount"] = "100",
        ["releaseMode"] = "CountOrMetadataDeadline", ["batchTimeMetadataKey"] = "BatchTime", ["batchIntervalMetadataKey"] = "rules.BatchInterval", ["timeZoneId"] = "UTC"
    } };
    private static IntegrationPayload Payload(string body, string time = "10:15", string interval = "NONE", string group = "A")
        => new(body + ".txt", BinaryData.FromString(body), "text/plain", new Dictionary<string, string>
        { ["rules.GroupID"] = group, ["BatchTime"] = time, ["rules.BatchInterval"] = interval });
    private static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
    private static DurableBatchAugmentModule Module(IFlowOrchestrationStateStore store, TimeProvider clock)
        => new(store, clock, NullLogger<DurableBatchAugmentModule>.Instance);
    private static Task<IntegrationBatch> Append(DurableBatchAugmentModule module, string execution, IntegrationPayload payload, ModuleStepDefinition? step = null)
        => module.AugmentAsync(new("batches", execution, DateTimeOffset.UtcNow), new([payload]), step ?? Collector(), default);

    [Theory]
    [InlineData("NONE", "UTC", "2026-04-01T09:10:00Z", "2026-04-01T10:15:00Z")]
    [InlineData("NONE", "UTC", "2026-04-01T10:15:00Z", "2026-04-02T10:15:00Z")]
    [InlineData("1:D", "UTC", "2026-04-01T09:10:00Z", "2026-04-02T09:10:00Z")]
    [InlineData("2:H", "UTC", "2026-04-01T09:10:00Z", "2026-04-01T11:10:00Z")]
    [InlineData("3:M", "UTC", "2026-04-01T09:10:00Z", "2026-04-01T09:13:00Z")]
    [InlineData("1:D", "W. Europe Standard Time", "2026-03-28T11:00:00Z", "2026-03-29T10:00:00Z")]
    [InlineData("1:H", "W. Europe Standard Time", "2026-10-25T00:15:00Z", "2026-10-25T02:15:00Z")]
    [InlineData("1:H", "W. Europe Standard Time", "2026-03-29T00:30:00Z", "2026-03-29T01:00:00Z")]
    [InlineData("1:H", "W. Europe Standard Time", "2026-03-29T00:30:45Z", "2026-03-29T01:00:00Z")]
    [InlineData("2:M", "UTC", "2026-04-01T09:10:59Z", "2026-04-01T09:12:59Z")]
    public async Task FirstMessagePinsPolicyOverrideWithExplicitLocalClockSemantics(string interval, string zone, string now, string expected)
    {
        var step = Collector(); step.Settings["timeZoneId"] = zone; var store = new InMemoryFlowOrchestrationStateStore();
        await Append(Module(store, new Clock(Date(now))), "first", Payload("one", interval: interval), step);
        var saved = Assert.Single(store.Checkpoints).Batch!;
        Assert.Equal(Date(expected), saved.DeadlineUtc); Assert.Equal("10:15", saved.OriginalBatchTime); Assert.Equal(interval, saved.PolicyBatchInterval);
    }

    [Fact]
    public async Task LaterBatchTimeUsesExistingDeadlineDateAndFirstMessageComparison_NotASlidingTimeout()
    {
        var clock = new Clock(Date("2026-04-01T20:30:00Z")); var store = new InMemoryFlowOrchestrationStateStore(); var module = Module(store, clock);
        await Append(module, "one", Payload("first", "10:15"));
        Assert.Equal(Date("2026-04-02T10:15:00Z"), Assert.Single(store.Checkpoints).Batch!.DeadlineUtc);
        clock.Now = clock.Now.AddMinutes(1); await Append(module, "two", Payload("second", "11:00"));
        Assert.Equal(Date("2026-04-02T11:00:00Z"), Assert.Single(store.Checkpoints).Batch!.DeadlineUtc);
        clock.Now = clock.Now.AddMinutes(1); await Append(module, "three", Payload("third", "10:15"));
        Assert.Equal(Date("2026-04-02T11:00:00Z"), Assert.Single(store.Checkpoints).Batch!.DeadlineUtc);
        Assert.Equal("10:15", Assert.Single(store.Checkpoints).Batch!.OriginalBatchTime);
    }

    [Fact]
    public async Task ChangedBatchTimeCanReleaseImmediately_AndRetryDoesNotAppendOrRecalculate()
    {
        var clock = new Clock(Date("2026-04-01T09:10:00Z")); var store = new InMemoryFlowOrchestrationStateStore(); var module = Module(store, clock);
        await Append(module, "one", Payload("first")); clock.Now = clock.Now.AddMinutes(5);
        var released = await Append(module, "two", Payload("second", "09:00"));
        Assert.Equal(2, released.Count); Assert.Equal("2", released.Payloads[0].Metadata["batch.count"]);
        var saved = Assert.Single(store.Checkpoints); Assert.True(saved.Batch!.Ready); var deadline = saved.Batch.DeadlineUtc;
        clock.Now = clock.Now.AddDays(1);
        var retried = await Append(module, "two", Payload("second", "garbage", "garbage"));
        Assert.Equal(2, retried.Count); Assert.Equal(deadline, Assert.Single(store.Checkpoints).Batch!.DeadlineUtc);
    }

    [Fact]
    public async Task IntervalOverrideDoesNotSlideOrAcceptLaterPolicyChanges()
    {
        var clock = new Clock(Date("2026-04-01T09:10:00Z")); var store = new InMemoryFlowOrchestrationStateStore(); var module = Module(store, clock);
        await Append(module, "one", Payload("first", interval: "2:H")); clock.Now = clock.Now.AddMinutes(5);
        await Append(module, "two", Payload("second", "not consulted", "NONE"));
        var saved = Assert.Single(store.Checkpoints).Batch!;
        Assert.Equal(Date("2026-04-01T11:10:00Z"), saved.DeadlineUtc); Assert.Equal("2:H", saved.PolicyBatchInterval);
    }

    [Theory]
    [InlineData("", "NONE")]
    [InlineData("9:00", "NONE")]
    [InlineData("24:00", "NONE")]
    [InlineData("10:00", "")]
    [InlineData("10:00", "0:M")]
    [InlineData("10:00", "-1:D")]
    [InlineData("10:00", "1:m")]
    [InlineData("10:00", "1:W")]
    [InlineData("10:00", "not-a-number:D")]
    [InlineData("10:00", "367:D")]
    [InlineData("10:00", "2147483647:H")]
    public async Task InvalidFirstMessageTimingNeverCreatesCheckpoint(string time, string interval)
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var module = Module(store, new Clock(Date("2026-04-01T09:10:00Z")));
        await Assert.ThrowsAsync<ArgumentException>(() => Append(module, "one", Payload("first", time, interval)));
        Assert.Empty(store.Checkpoints);
    }

    [Fact]
    public async Task InvalidLaterMessageOrChangedMetadataSettingsNeverReplaceHeldData()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var module = Module(store, new Clock(Date("2026-04-01T09:10:00Z")));
        await Append(module, "one", Payload("first")); var before = JsonSerializer.Serialize(Assert.Single(store.Checkpoints));
        await Assert.ThrowsAsync<ArgumentException>(() => Append(module, "two", Payload("second", "invalid")));
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(store.Checkpoints)));
        var changed = Collector(); changed.Settings["batchTimeMetadataKey"] = "different-key";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Append(module, "two", Payload("second"), changed));
        Assert.Equal(before, JsonSerializer.Serialize(Assert.Single(store.Checkpoints)));
    }

    [Theory]
    [InlineData("batchTimeMetadataKey", "")]
    [InlineData("batchIntervalMetadataKey", "")]
    [InlineData("batchIntervalMetadataKey", "batchtime")]
    [InlineData("timeZoneId", "")]
    public void MetadataScheduleRequiresExplicitDistinctKeysAndTimezone(string key, string value)
    {
        var step = Collector(); step.Settings[key] = value;
        Assert.Throws<ArgumentException>(() => DurableBatchSchedule.Read(step));
    }

    [Fact]
    public async Task PoliciesDriveIndependentDeadlinesAcrossDiskRestart_AndIdleReleaseDeliversThenAcknowledgesWithoutRefetch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-policy-deadline-" + Guid.NewGuid().ToString("N"));
        var providers = new List<ServiceProvider>();
        try
        {
            Directory.CreateDirectory(directory);
            var xsltPath = Path.Combine(directory, "counted-batch.xslt");
            await File.WriteAllTextAsync(xsltPath, """
                <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:b="urn:mulse:batch">
                  <xsl:output method="text"/><xsl:param name="client"/>
                  <xsl:template match="/"><xsl:value-of select="$client"/><xsl:text>:</xsl:text>
                    <xsl:for-each select="/b:Batch/b:Messages/b:Message/Billing"><xsl:if test="position()!=1"><xsl:text>|</xsl:text></xsl:if><xsl:value-of select="."/></xsl:for-each>
                    <xsl:text>|T</xsl:text><xsl:value-of select="/b:Batch/b:MessageCount"/>
                  </xsl:template>
                </xsl:stylesheet>
                """);
            var clock = new Clock(Date("2026-04-01T09:10:00Z"));
            var source = new Source(new([Payload("<Billing>a1</Billing>")]), new([Payload("<Billing>b1</Billing>", group: "B")]), new([Payload("<Billing>a2</Billing>", "11:00")]));
            var delivered = new List<IntegrationBatch>(); var policy = new XmlRulePolicy
            {
                Name = "Synthetic client batch intervals", Version = "1.0", RootName = "Facts", Rules =
                [new() { Name = "A", Conditions = [new() { Name = "GroupID", Value = "A" }], Assignments = [new() { Name = "BatchInterval", Value = "1:H" }, new() { Name = "MapName", Value = "map.A" }] },
                 new() { Name = "B", Conditions = [new() { Name = "GroupID", Value = "B" }], Assignments = [new() { Name = "BatchInterval", Value = "30:M" }, new() { Name = "MapName", Value = "map.B" }] }]
            };
            var choices = JsonSerializer.Serialize(new[] { "A", "B" }.Select(client => new RenderDispatchCase
            {
                Value = "map." + client, Module = "xslt-render", Settings = new() { ["xsltPath"] = xsltPath, ["outputExtension"] = ".txt", ["parametersJson"] = JsonSerializer.Serialize(new { client }) }
            }), JsonSerializerOptions.Web);
            var flow = new PipelineDefinition
            {
                Id = "batches", Enabled = true,
                Fetches = [new() { Id = "f", Fetch = new() { Module = "source" } }],
                Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
                Augments = [new() { Module = "xml-rules-augment", Settings = new() { ["factMode"] = "Metadata", ["policyJson"] = JsonSerializer.Serialize(policy) } }, Collector(), new() { Module = "xml-batch-envelope-augment" }],
                Deliveries = [new() { Render = new() { Module = "render-dispatch", Settings = new() { ["metadataKey"] = "rules.MapName", ["casesJson"] = choices } }, Delivers = [new() { Deliver = new() { Module = "destination" } }] }]
            };
            RuntimeConfigurationStore Disk() => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
            FlowRuntime Restart()
            {
                var disk = Disk(); var checkpoints = new FlowOrchestrationStateStore(disk);
                var catalog = new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
                    .AddAugment(new XmlRulesAugmentModule()).AddAugment(Module(checkpoints, clock)).AddAugment(new XmlBatchEnvelopeAugmentModule()).AddRender(new XsltRenderModule())
                    .AddDeliver(DelegateModuleFactory.Deliver("destination", delivered));
                var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
                providers.Add(services);
                catalog.AddRender(new RenderDispatchModule(new Service.Modules.RenderModuleInvoker(services)));
                return new(new FakeFlowDefinitionService(flow), catalog, checkpoints, new FlowRetryStateStore(disk),
                    new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), clock, NullLogger<FlowRuntime>.Instance);
            }
            Assert.Equal(FlowExecutionOutcome.Suspended, (await Restart().ExecuteAsync(flow.Id, default)).Outcome);
            Assert.Equal(FlowExecutionOutcome.Suspended, (await Restart().ExecuteAsync(flow.Id, default)).Outcome);
            clock.Now = clock.Now.AddMinutes(5); Assert.Equal(FlowExecutionOutcome.Suspended, (await Restart().ExecuteAsync(flow.Id, default)).Outcome);
            Assert.Empty(source.Outcomes); Assert.Empty(delivered);
            var held = Disk().GetState().OrchestrationCheckpoints;
            Assert.Equal("1:H", held.Single(c => c.CorrelationKey == "A").Batch!.PolicyBatchInterval);
            Assert.Equal(Date("2026-04-01T10:10:00Z"), held.Single(c => c.CorrelationKey == "A").Batch!.DeadlineUtc);
            clock.Now = Date("2026-04-01T09:40:00Z");
            Assert.Equal(FlowExecutionOutcome.Completed, (await Restart().ReleaseDueBatchAsync(flow.Id, default))!.Outcome);
            Assert.Equal("B:b1|T1", Assert.Single(delivered[0].Payloads).GetText()); Assert.Single(source.Outcomes);
            clock.Now = Date("2026-04-01T10:10:00Z");
            Assert.Equal(FlowExecutionOutcome.Completed, (await Restart().ReleaseDueBatchAsync(flow.Id, default))!.Outcome);
            Assert.Equal("A:a1|a2|T2", Assert.Single(delivered[1].Payloads).GetText()); Assert.Equal("2", delivered[1].Payloads[0].Metadata["batch.count"]);
            Assert.Equal(3, source.Fetches); Assert.Equal(3, source.Outcomes.Count); Assert.All(source.Outcomes, outcome => Assert.True(outcome.Succeeded));
            Assert.Empty(Disk().GetState().OrchestrationCheckpoints); Assert.Null(await Restart().ReleaseDueBatchAsync(flow.Id, default));
        }
        finally
        {
            foreach (var provider in providers) await provider.DisposeAsync();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task EarlyReleasePreservesPinnedTimingAndNoFetchesAreAdded()
    {
        var clock = new Clock(Date("2026-04-01T09:10:00Z")); var store = new InMemoryFlowOrchestrationStateStore();
        await Append(Module(store, clock), "one", Payload("first", interval: "2:H"));
        var saved = Assert.Single(store.Checkpoints);
        DurableBatchAugmentModule.ValidateCheckpointSettings(saved, Collector());
        var flow = new PipelineDefinition
        {
            Id = "batches", Enabled = true,
            Fetches = [new() { Id = "f", Fetch = new() { Module = "source" } }],
            Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }], Augments = [Collector()],
            Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "destination" } }] }]
        };
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("source", (_, _, _, _) => throw new InvalidOperationException("Manual release must not fetch.")))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse")).AddAugment(Module(store, clock))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(DelegateModuleFactory.Deliver("destination", failOnCall: _ => true));
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, store, new InMemoryFlowRetryStateStore(),
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), clock, NullLogger<FlowRuntime>.Instance);
        var result = await runtime.ReleaseCollectedBatchAsync(flow.Id, saved.InstanceId, 1, default);
        Assert.Contains("destination", result.Detail!);
        var frozen = Assert.Single(store.Checkpoints).Batch!;
        Assert.True(frozen.Ready); Assert.Equal(saved.Batch!.DeadlineUtc, frozen.DeadlineUtc);
        Assert.Equal("10:15", frozen.OriginalBatchTime); Assert.Equal("2:H", frozen.PolicyBatchInterval);
        var corrupt = new FlowOrchestrationCheckpoint { Batch = new() { CollectorId = "billing", MetadataKey = "rules.GroupID", TargetCount = 100, ScheduleKey = saved.Batch.ScheduleKey, DeadlineUtc = saved.Batch.DeadlineUtc } };
        Assert.Throws<InvalidOperationException>(() => DurableBatchAugmentModule.ValidateCheckpointSettings(corrupt, Collector()));
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
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
}
