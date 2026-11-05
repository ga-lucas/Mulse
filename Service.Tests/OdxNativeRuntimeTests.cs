using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.CompatibilityPack;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.Mapping;
using Mulse.Modules.Orchestration;

namespace Service.Tests;

public sealed class OdxNativeRuntimeTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string type, string name, params object[] children) => new(Ns + "Element", new XAttribute("Type", type), P("Name", name), children);
    private static OdxProgram Program() => OdxProgramReader.Read("<?xml version='1.0'?>" + new XElement(Ns + "MetaModel",
        N("ServiceDeclaration", "Service", N("ServiceBody", "Body",
            N("Receive", "Input", P("PortName", "request"), P("MessageName", "input")),
            N("Delay", "Wait", P("Timeout", "Source expression requires explicit native timer binding")),
            N("MessageAssignment", "Map", P("Expression", "mapType = System.Type.GetType(policy.MapName); transform (output) = mapType(input);")),
            N("Send", "Reply", P("PortName", "request"), P("MessageName", "output"), P("OperationMessageName", "Response"))))));
    private static ModuleStepDefinition Step(OdxProgram program, string map) => new() { Module = OdxProgramAugmentModule.ModuleId, Settings = new()
    {
        ["programJson"] = JsonSerializer.Serialize(program, JsonSerializerOptions.Web), ["initialFactsJson"] = "{\"policy\":{\"MapName\":\"Example.Map\"}}",
        ["correlationMetadataKey"] = "group", ["messageIdMetadataKey"] = "messageId", ["inputPort"] = "request", ["receiveCorrelationKeysJson"] = "[\"group\"]",
        ["delaysJson"] = JsonSerializer.Serialize(new[] { new { nodeId = program.Roots[0].Children[0].Children[1].Id, seconds = 60 } }),
        ["casesJson"] = JsonSerializer.Serialize(new[] { new RenderDispatchCase { Value = "Example.Map", Module = "xslt-render", Settings = new() { ["xsltPath"] = map } } }, JsonSerializerOptions.Web),
        ["outputMessage"] = "output", ["replyPort"] = "request"
    } };
    private static PipelineDefinition Flow(ModuleStepDefinition step, bool retryDelivery = false, RetryPolicyDefinition? defaultRetry = null) => new()
    {
        Id = "native-odx", Enabled = true, Retry = defaultRetry,
        Fetches = [new() { Id = "f", Fetch = new() { Module = "source" } }],
        Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }], Augments = [step],
        Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "destination",
            Retry = retryDelivery ? new() { MaxAttempts = 2, Delay = TimeSpan.Zero } : null } }] }]
    };
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Source : IFetchOutcomeModule
    {
        public ModuleDescriptor Descriptor { get; } = new("source", "Source", ModuleKind.Fetch, "test");
        public int Fetches { get; private set; }
        public string Group { get; set; } = "A";
        public string MessageId { get; set; } = "source-1";
        public int CallbackFailures { get; set; }
        public int NotificationAttempts { get; private set; }
        public List<FetchProcessingOutcome> Outcomes { get; } = [];
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken token)
        {
            Fetches++;
            return Task.FromResult(new IntegrationBatch([new("input.xml", BinaryData.FromString("<Input><Value>42</Value></Input>"), "application/xml",
                new Dictionary<string, string> { ["group"] = Group, ["messageId"] = MessageId })]) { SourceReceipt = new Dictionary<string, string> { ["id"] = MessageId } });
        }
        public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken token)
        {
            NotificationAttempts++;
            if (CallbackFailures > 0) { CallbackFailures--; throw new IOException("Synthetic source acknowledgement failure"); }
            Outcomes.Add(outcome); return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 1)]
    [InlineData(false, false, 2)]
    [InlineData(false, false, 3)]
    public async Task NativeProgramResumesAtPersistedTimerAfterRestart_MapsRepliesAndAcknowledgesOnlyAfterDelivery(bool failDelivery, bool retryDelivery, int releaseMode)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-native-odx-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        var providers = new List<ServiceProvider>();
        try
        {
            var map = Path.Combine(directory, "map.xslt");
            await File.WriteAllTextAsync(map, """
                <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output method="xml" omit-xml-declaration="yes"/>
                <xsl:template match="/"><Result><xsl:value-of select="/Input/Value"/></Result></xsl:template></xsl:stylesheet>
                """);
            var retryNotification = releaseMode == 3;
            var flow = Flow(Step(Program(), map), retryDelivery, retryNotification ? new() { MaxAttempts = 2, Delay = TimeSpan.Zero } : null);
            var source = new Source { CallbackFailures = retryNotification ? 1 : 0 }; var clock = new Clock(); var delivered = new List<IntegrationBatch>();
            var deliveryCalls = 0;
            RuntimeConfigurationStore Disk() => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
            FlowRuntime Restart(bool enabled = true)
            {
                var disk = Disk(); var checkpoints = new FlowOrchestrationStateStore(disk);
                var catalog = new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
                    .AddRender(new XsltRenderModule()).AddRender(DelegateModuleFactory.PassthroughRender("render"))
                    .AddDeliver(new DelegateDeliverModule("destination", (_, batch, _, _) =>
                    {
                        if (++deliveryCalls == 1 && (failDelivery || retryDelivery)) throw new IOException("Synthetic destination failure");
                        delivered.Add(batch); return Task.CompletedTask;
                    }));
                var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider(); providers.Add(provider);
                catalog.AddAugment(new OdxProgramAugmentModule(checkpoints, clock, new Service.Modules.RenderModuleInvoker(provider)));
                return new(new FakeFlowDefinitionService(enabled ? flow : FlowMappings.WithEnabled(flow, false)), catalog, checkpoints, new FlowRetryStateStore(disk),
                    new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), clock, NullLogger<FlowRuntime>.Instance);
            }
            Assert.Equal(FlowExecutionOutcome.Suspended, (await Restart().ExecuteAsync(flow.Id, default)).Outcome);
            var waiting = Assert.Single(Disk().GetState().OrchestrationCheckpoints);
            Assert.Equal(clock.Now.AddMinutes(1), waiting.Odx!.DeadlineUtc); Assert.False(waiting.Odx.RecoveryPending);
            Assert.Single(waiting.SourceReceipts); Assert.Empty(source.Outcomes); Assert.Empty(delivered);
            Assert.Null(await Restart().ReleaseDueBatchAsync(flow.Id, default));
            clock.Now = clock.Now.AddMinutes(1);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Restart(enabled: false).ReleaseDueBatchAsync(flow.Id, default));
            Assert.Empty(delivered); Assert.Single(Disk().GetState().OrchestrationCheckpoints);
            FlowExecutionResult? result;
            if (releaseMode == 1)
            {
                var runtime = Restart();
                result = Assert.Single(await Task.WhenAll(runtime.ReleaseDueBatchAsync(flow.Id, default), runtime.ReleaseDueBatchAsync(flow.Id, default)), item => item is not null);
            }
            else if (releaseMode == 2)
            {
                var observed = new ObservedRuntime(Restart());
                using var driver = new Service.Triggers.BatchDeadlineFlowService(observed, NullLogger<Service.Triggers.BatchDeadlineFlowService>.Instance);
                await driver.StartAsync(default);
                try { result = await observed.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
                finally { await driver.StopAsync(default); }
            }
            else result = await Restart().ReleaseDueBatchAsync(flow.Id, default);
            if (retryDelivery || retryNotification)
            {
                Assert.Equal(FlowExecutionOutcome.Retrying, result!.Outcome); Assert.Empty(source.Outcomes);
                Assert.Null(await Restart().ReleaseDueBatchAsync(flow.Id, default));
                await File.WriteAllTextAsync(map, "Map must not execute again during downstream recovery.");
                var pendingRetry = Assert.Single(Disk().GetState().PendingRetries);
                if (retryNotification) Assert.Equal("source-outcome", pendingRetry.StageId);
                result = await Restart().ResumeRetryAsync(pendingRetry, default);
                Assert.Equal(retryDelivery ? 2 : 1, deliveryCalls);
            }
            Assert.Equal(FlowExecutionOutcome.Completed, result!.Outcome); Assert.Equal(1, source.Fetches);
            Assert.Equal(retryNotification ? 2 : 1, source.NotificationAttempts);
            Assert.Equal("42", XDocument.Parse(Assert.Single(result.ResponsePayloads!).GetText()).Root!.Value);
            Assert.Equal(!failDelivery, Assert.Single(source.Outcomes).Succeeded);
            if (failDelivery)
            {
                Assert.True(Assert.Single(Disk().GetState().OrchestrationCheckpoints).Odx!.RecoveryPending);
                Assert.Null(await Restart().ReleaseDueBatchAsync(flow.Id, default));
                await Assert.ThrowsAsync<InvalidOperationException>(() => Restart().ExecuteAsync(flow.Id, default));
                Assert.Equal(1, source.Fetches);
            }
            else
            {
                Assert.Equal("42", XDocument.Parse(Assert.Single(Assert.Single(delivered).Payloads).GetText()).Root!.Value);
                Assert.Empty(Disk().GetState().OrchestrationCheckpoints); Assert.Null(await Restart().ReleaseDueBatchAsync(flow.Id, default));
            }
        }
        finally { foreach (var provider in providers) await provider.DisposeAsync(); Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NativeCountedTrailerSourceMapsRepliesDeliversAndAcknowledgesWithoutPerStatementDelegates()
    {
        var path = Path.Combine(Path.GetTempPath(), "mulse-native-trailer-" + Guid.NewGuid().ToString("N") + ".xslt");
        try
        {
            await File.WriteAllTextAsync(path, """
                <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output omit-xml-declaration="yes"/>
                <xsl:template match="/"><Mapped><xsl:value-of select="/Trailer/Count"/></Mapped></xsl:template></xsl:stylesheet>
                """);
            var program = OdxProgramReader.Read("<?xml version='1.0'?>" + new XElement(Ns + "MetaModel", N("ServiceDeclaration", "Service", N("ServiceBody", "Body",
                N("Receive", "Input", P("PortName", "request"), P("MessageName", "input")),
                N("MessageAssignment", "Trailer", P("Expression", """
                    /* Counted XML trailer source. */
                    doc = new System.Xml.XmlDocument();
                    doc.LoadXml("<Trailer><Count>" + count.ToString() + "</Count></Trailer>");
                    output = doc;
                    tMap = System.Type.GetType(policy.MapName);
                    transform (output) = tMap(output);
                    """)),
                N("Send", "Reply", P("PortName", "request"), P("MessageName", "output"), P("OperationMessageName", "Response"))))));
            var step = Step(program, path); step.Settings["delaysJson"] = "[]";
            step.Settings["initialFactsJson"] = "{\"policy\":{\"MapName\":\"Example.Map\"},\"count\":3}";
            var source = new Source(); var configuration = new FakeRuntimeConfigurationStore(new()); var checkpoints = new FlowOrchestrationStateStore(configuration);
            var delivered = new List<IntegrationBatch>(); var retries = new InMemoryFlowRetryStateStore();
            var catalog = new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
                .AddRender(new XsltRenderModule()).AddRender(DelegateModuleFactory.PassthroughRender("render"))
                .AddDeliver(DelegateModuleFactory.Deliver("destination", delivered));
            await using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
            catalog.AddAugment(new OdxProgramAugmentModule(checkpoints, TimeProvider.System, new Service.Modules.RenderModuleInvoker(services)));
            var flow = Flow(step); var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, checkpoints, retries,
                new ConfigValueResolver(configuration), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
            var result = await runtime.ExecuteAsync(flow.Id, default);
            Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
            Assert.Equal("3", XDocument.Parse(Assert.Single(result.ResponsePayloads!).GetText()).Root!.Value);
            Assert.Equal("3", XDocument.Parse(Assert.Single(Assert.Single(delivered).Payloads).GetText()).Root!.Value);
            Assert.True(Assert.Single(source.Outcomes).Succeeded); Assert.Empty(await checkpoints.GetAllAsync(flow.Id, default)); Assert.Empty(retries.States);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AmbiguousRendererEffectRetainsReceiptsWithoutCallbacksOrGenericStageRetries()
    {
        var source = new Source(); var clock = new Clock(); var maps = 0;
        var configuration = new FakeRuntimeConfigurationStore(new()); var checkpoints = new FlowOrchestrationStateStore(configuration);
        var retries = new InMemoryFlowRetryStateStore();
        var catalog = new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(new DelegateRenderModule("xslt-render", (_, _, _, _) => { maps++; throw new IOException("Ambiguous native renderer effect"); }))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(DelegateModuleFactory.Deliver("destination", new List<IntegrationBatch>()));
        await using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddAugment(new OdxProgramAugmentModule(checkpoints, clock, new Service.Modules.RenderModuleInvoker(services)));
        var flow = Flow(Step(Program(), "explicit-adapter"), defaultRetry: new() { MaxAttempts = 3, Delay = TimeSpan.Zero });
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, checkpoints, retries,
            new ConfigValueResolver(configuration), clock, NullLogger<FlowRuntime>.Instance);
        Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
        clock.Now = clock.Now.AddMinutes(1);
        await Assert.ThrowsAsync<OdxAmbiguousEffectException>(() => runtime.ReleaseDueBatchAsync(flow.Id, default));
        var held = Assert.Single(await checkpoints.GetAllAsync(flow.Id, default));
        Assert.True(held.Odx!.RecoveryPending); Assert.Single(held.SourceReceipts);
        Assert.Empty(source.Outcomes); Assert.Empty(retries.States); Assert.Equal(1, maps);
        Assert.Null(await runtime.ReleaseDueBatchAsync(flow.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(flow.Id, default));
        Assert.Equal(1, source.Fetches); Assert.Equal(1, maps); Assert.Empty(source.Outcomes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnmatchedInputCannotAcknowledgeCurrentOrRetainedReceiptsOrCreateAnEmptyActivation(bool existingInstance)
    {
        var source = new Source(); var clock = new Clock(); var configuration = new FakeRuntimeConfigurationStore(new());
        var checkpoints = new FlowOrchestrationStateStore(configuration); var retries = new InMemoryFlowRetryStateStore();
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(new DelegateRenderModule("xslt-render", (_, _, _, _) => throw new InvalidOperationException("Unmatched input must not run a map.")))
            .AddRender(DelegateModuleFactory.PassthroughRender("render")).AddDeliver(DelegateModuleFactory.Deliver("destination", delivered));
        await using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddAugment(new OdxProgramAugmentModule(checkpoints, clock, new Service.Modules.RenderModuleInvoker(services)));
        var step = Step(Program(), "unexecuted-map");
        if (!existingInstance)
        {
            var filtered = OdxProgramReader.Read("<?xml version='1.0'?>" + new XElement(Ns + "MetaModel", N("ServiceDeclaration", "Service", N("ServiceBody", "Body",
                N("Receive", "Input", P("PortName", "request"), P("MessageName", "input"), N("DNFPredicate", "RequiredPort", P("LHS", "BTS.ReceivePortName"), P("Grouping", "AND"), P("Operator", "Exists")))))));
            step.Settings["programJson"] = JsonSerializer.Serialize(filtered, JsonSerializerOptions.Web); step.Settings["delaysJson"] = "[]";
        }
        var flow = Flow(step, defaultRetry: new() { MaxAttempts = 3, Delay = TimeSpan.Zero });
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, checkpoints, retries,
            new ConfigValueResolver(configuration), clock, NullLogger<FlowRuntime>.Instance);
        if (existingInstance)
        {
            Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
            source.MessageId = "source-2";
        }
        await Assert.ThrowsAsync<OdxUnmatchedMessageException>(() => runtime.ExecuteAsync(flow.Id, default));
        var retained = await checkpoints.GetAllAsync(flow.Id, default);
        if (existingInstance) Assert.Single(Assert.Single(retained).SourceReceipts); else Assert.Empty(retained);
        Assert.Empty(source.Outcomes); Assert.Equal(0, source.NotificationAttempts); Assert.Empty(retries.States); Assert.Empty(delivered);
    }

    [Fact]
    public async Task CaseDistinctClientValuesHaveIndependentInstancesInTheCaseInsensitiveHostStore()
    {
        var source = new Source(); var clock = new Clock();
        var configuration = new FakeRuntimeConfigurationStore(new()); var checkpoints = new FlowOrchestrationStateStore(configuration);
        var catalog = new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(new XsltRenderModule()).AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(DelegateModuleFactory.Deliver("destination", new List<IntegrationBatch>()));
        await using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddAugment(new OdxProgramAugmentModule(checkpoints, clock, new Service.Modules.RenderModuleInvoker(services)));
        var flow = Flow(Step(Program(), "not-invoked-before-deadline.xslt"));
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, checkpoints, new InMemoryFlowRetryStateStore(),
            new ConfigValueResolver(configuration), clock, NullLogger<FlowRuntime>.Instance);
        Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
        source.Group = "a";
        Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
        var held = await checkpoints.GetAllAsync(flow.Id, default);
        Assert.Equal(2, held.Count); Assert.Equal(2, held.Select(checkpoint => checkpoint.CorrelationKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(held, checkpoint => checkpoint.Payloads.Single().Metadata["group"] == "A");
        Assert.Contains(held, checkpoint => checkpoint.Payloads.Single().Metadata["group"] == "a");
        Assert.Equal(2, source.Fetches); Assert.Empty(source.Outcomes);
    }

    private sealed class ObservedRuntime(IFlowRuntime inner) : IFlowRuntime
    {
        public TaskCompletionSource<FlowExecutionResult?> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<PipelineDefinition> GetConfiguredFlows() => inner.GetConfiguredFlows();
        public async Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flow, CancellationToken token)
        {
            var result = await inner.ReleaseDueBatchAsync(flow, token); Completed.TrySetResult(result); return result;
        }
        public Task<FlowExecutionResult> ExecuteAsync(string flow, CancellationToken token) => throw new NotSupportedException("Deadline polling must not fetch.");
        public Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flow, string instance, int count, CancellationToken token) => throw new NotSupportedException("Deadline polling must not force release.");
        public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState state, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flow, CancellationToken token) => throw new NotSupportedException();
    }

    [Fact]
    public void DynamicChoicesAreDependencyTracked_AndStatefulMixesAndPendingEditsAreBlocked()
    {
        var step = Step(Program(), "map.xslt"); var flow = Flow(step);
        Assert.Contains(("xslt-render", ModuleKind.Render), FlowModuleDependencies.References(flow));
        var noMaps = new ModuleStepDefinition { Module = OdxProgramAugmentModule.ModuleId };
        Assert.Empty(FlowModuleDependencies.FindMissing(Flow(noMaps), FakeModuleCatalog.WithModules(("source", ModuleKind.Fetch), ("parse", ModuleKind.Parse),
            (OdxProgramAugmentModule.ModuleId, ModuleKind.OrchestrationAugment), ("render", ModuleKind.Render), ("destination", ModuleKind.Deliver)).GetAll()));
        var mixed = Flow(step); mixed.Augments.Add(new() { Module = "stateful-orchestration-augment" });
        Assert.Throws<ArgumentException>(() => DurableBatchSafety.Validate(mixed));
        var changed = Flow(Step(Program(), "different.xslt"));
        Assert.Throws<InvalidOperationException>(() => DurableBatchSafety.EnsureUnchanged(flow, changed, [new() { FlowId = flow.Id, Odx = new() } ]));
    }
}
