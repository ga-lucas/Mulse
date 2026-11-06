using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Logging;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Pipeline;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;
using Service.BizTalkImport;
using Service.Execution;
using Service.Models;
using Service.Modules;

namespace Service.Tests;

public sealed class NativeImportAndHostTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-native-import-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128, Converters = { new JsonStringEnumConverter() } };
    private static XElement P(string name, string value) => new(Ns + "Property", new XAttribute("Name", name), new XAttribute("Value", value));
    private static XElement N(string kind, params object[] children) => new(Ns + "Element", new XAttribute("Type", kind), children);
    private async Task<BizTalkFlowCandidateResponse> Import(bool unsupported = false, bool ambiguous = false, bool pipeline = false, string? delay = null)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Example.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Example</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        var body = new List<object>
        {
            N("Receive", P("PortName", "In"), P("MessageName", "input")),
            N("Construct", N("MessageAssignment", P("Expression", "output = input;"))),
            N("Send", P("PortName", "Out"), P("MessageName", "output")),
            N("Send", P("PortName", "In"), P("MessageName", "output"), P("OperationMessageName", "Response"))
        };
        if (delay is not null) body.Insert(2, N("Delay", P("Name", "Pause"), P("Timeout", delay)));
        if (unsupported) body.Insert(2, N("AtomicTransaction", N("Expression", P("Expression", "Custom.Helper.Run();"))));
        var root = new XElement(Ns + "MetaModel", N("ServiceDeclaration", P("Name", "Flow"), N("ServiceBody", body.ToArray())));
        if (unsupported) root.Add(N("ExternalVariable", P("Name", "global-fact")));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + root);
        var binding = Path.Combine(_directory, "bindings.xml");
        await File.WriteAllTextAsync(binding, $"""
            <BindingInfo><ModuleRefCollection><ModuleRef Name="Example"><Services><Service Name="Example.Flow"><Ports>
            <Port Name="In"><ReceivePortRef Name="Input"/></Port><Port Name="Out"><SendPortRef Name="Output"/>{(ambiguous ? "<SendPortRef Name='OtherOutput'/>" : "")}</Port>
            </Ports></Service></Services></ModuleRef></ModuleRefCollection>
            <ReceivePortCollection><ReceivePort Name="Input"><ReceiveLocations><ReceiveLocation Name="Source"><Address>C:\synthetic\input</Address><TransportType Name="FILE"/></ReceiveLocation></ReceiveLocations></ReceivePort></ReceivePortCollection>
            <SendPortCollection><SendPort Name="Output"><PrimaryTransport><Address>C:\synthetic\output</Address><TransportType Name="FILE"/></PrimaryTransport>{(pipeline ? "<TransmitPipeline Name='Example.CustomSend, Example'/>" : "")}</SendPort>
            <SendPort Name="OtherOutput"><PrimaryTransport><Address>C:\synthetic\other</Address><TransportType Name="FILE"/></PrimaryTransport></SendPort></SendPortCollection></BindingInfo>
            """);
        return Assert.Single((await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
            { SourcePath = project, AdditionalBindingPaths = [binding], GenerateNativeFlow = true }, default)).FlowCandidates);
    }

    [Fact]
    public async Task ImportPreservesSourceOrderNamedCopiesExactPortAndNativeReply_WithoutOdxExecution()
    {
        var candidate = await Import();
        Assert.False(candidate.DraftFlow.Enabled); Assert.Empty(candidate.DraftFlow.Deliveries);
        var step = Assert.Single(candidate.DraftFlow.Augments); Assert.Equal(NativeFlowDefinition.ModuleId, step.Module);
        var definition = NativeFlowDefinition.Read(step.Settings["flowJson"]);
        Assert.True(definition.Durable);
        Assert.Equal(new[] { NativeFlowNodeKind.Receive, NativeFlowNodeKind.UseMessage, NativeFlowNodeKind.SaveMessage, NativeFlowNodeKind.Deliver, NativeFlowNodeKind.Reply },
            definition.AllNodes().Where(node => node.Kind != NativeFlowNodeKind.Sequence).Select(node => node.Kind));
        var outbound = definition.AllNodes().Single(node => node.Kind == NativeFlowNodeKind.Deliver);
        Assert.Equal("file-system-deliver", outbound.Module); Assert.Equal("C:\\synthetic\\output", outbound.Settings["path"]); Assert.Equal("output", outbound.MessageName);
        Assert.Contains(candidate.ScaffoldedModules, module => module.FileName.EndsWith("-native.json", StringComparison.Ordinal));
        var observed = new List<IntegrationBatch>(); var source = new Source(); var clock = new Clock();
        var checkpoints = new InMemoryFlowOrchestrationStateStore(); var retries = new InMemoryFlowRetryStateStore();
        var catalog = new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddDeliver(DelegateModuleFactory.Deliver("file-system-deliver", observed));
        await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddAugment(new NativeControlFlowAugmentModule(new AugmentModuleInvoker(provider), new RenderModuleInvoker(provider), checkpoints, clock, new DeliverModuleInvoker(provider)));
        var flow = Flow(new() { Module = step.Module, Settings = new(step.Settings) });
        var runtime = Runtime(flow, catalog, checkpoints, retries, clock);
        var result = await runtime.ExecuteAsync(flow.Id, default);
        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        Assert.Equal("{\"value\":42}", Assert.Single(Assert.Single(observed).Payloads).GetText());
        Assert.Equal("{\"value\":42}", Assert.Single(result.ResponsePayloads!).GetText());
        Assert.True(Assert.Single(source.Outcomes).Succeeded); Assert.Empty(await checkpoints.GetAllAsync(flow.Id, default));
        Assert.Throws<Service.Flows.FlowMigrationBlockedException>(() => Service.Flows.FlowMigrationReadiness.EnsureReady(new() { Id = "imported", Migration = candidate.DraftFlow.Migration }));
    }

    [Fact]
    public async Task ImportedTickWaitCanBeEditedAndReopenedInNativeEditor()
    {
        var candidate = await Import(delay: "TimeSpan.FromTicks(20000000L)");
        var original = candidate.DraftFlow.Augments.Single().Settings["flowJson"];
        string saved = "";
        var modules = new ModuleViewModel[] { new("file-system-deliver", "File", "Deliver", "Test", [], [], [], []) };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () => await renderer.RenderComponentAsync<ImportedWaitEditHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Value"] = original, ["Modules"] = modules, ["ValueChanged"] = EventCallback.Factory.Create<string>(this, value => saved = value) })));
        Assert.Equal(3, Assert.Single(NativeFlowDefinition.Read(saved).AllNodes(), node => node.Kind == NativeFlowNodeKind.Wait).DurationSeconds);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Value"] = saved, ["Modules"] = modules }))).ToHtmlString());
        Assert.Contains("Wait seconds", html); Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete", html);
        Assert.False(candidate.DraftFlow.Enabled);
        Assert.Throws<Service.Flows.FlowMigrationBlockedException>(() => Service.Flows.FlowMigrationReadiness.EnsureReady(new() { Id = "imported", Migration = candidate.DraftFlow.Migration }));
    }

    [Fact]
    public async Task ImportedTickWaitSuspendsAndResumesWithoutRefetchOrEarlyDelivery()
    {
        var candidate = await Import(delay: "new System.TimeSpan(20000000L)");
        var step = Assert.Single(candidate.DraftFlow.Augments);
        Assert.Equal(2, Assert.Single(NativeFlowDefinition.Read(step.Settings["flowJson"]).AllNodes(), node => node.Kind == NativeFlowNodeKind.Wait).DurationSeconds);
        var observed = new List<IntegrationBatch>(); var source = new Source(); var clock = new Clock();
        var checkpoints = new InMemoryFlowOrchestrationStateStore(); var retries = new InMemoryFlowRetryStateStore();
        var catalog = new FakeExecutableModuleCatalog().AddFetch(source).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddDeliver(DelegateModuleFactory.Deliver("file-system-deliver", observed));
        await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddAugment(new NativeControlFlowAugmentModule(new AugmentModuleInvoker(provider), new RenderModuleInvoker(provider), checkpoints, clock, new DeliverModuleInvoker(provider)));
        var flow = Flow(new() { Module = step.Module, Settings = new(step.Settings) });
        var runtime = Runtime(flow, catalog, checkpoints, retries, clock);
        Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
        Assert.Empty(observed); Assert.Empty(source.Outcomes);
        clock.Now = clock.Now.AddSeconds(1); Assert.Null(await runtime.ReleaseDueBatchAsync(flow.Id, default));
        clock.Now = clock.Now.AddSeconds(1);
        var completed = await Runtime(flow, catalog, checkpoints, retries, clock).ReleaseDueBatchAsync(flow.Id, default);
        Assert.Equal(FlowExecutionOutcome.Completed, completed!.Outcome);
        Assert.Equal(1, source.Fetches); Assert.Single(observed); Assert.True(Assert.Single(source.Outcomes).Succeeded);
        Assert.Empty(await checkpoints.GetAllAsync(flow.Id, default));
    }

    [Fact]
    public async Task TransmitPipelineRemainsBlockedWithEditableDeliveryAndNamedMessageGuidance()
    {
        var candidate = await Import(pipeline: true);
        var json = candidate.DraftFlow.Augments.Single().Settings["flowJson"];
        var definition = NativeFlowDefinition.Read(json, allowBlocked: true);
        var blocker = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Contains("Example.CustomSend"));
        Assert.Contains("named-message augment/render", blocker.BlockReason);
        var deliver = Assert.Single(blocker.Children);
        Assert.Equal(NativeFlowNodeKind.Deliver, deliver.Kind); Assert.Equal("output", deliver.MessageName);
        Assert.Contains(candidate.DraftFlow.Migration!.Diagnostics, diagnostic => diagnostic.Code == "BT-NATIVE-UNRESOLVED" && diagnostic.Message.Contains("Example.CustomSend"));
        Assert.Throws<NotSupportedException>(() => NativeFlowDefinition.Read(json));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnsupportedSourceAndAmbiguousBindingsRemainStructuredBlockers(bool unsupported, bool ambiguous)
    {
        var candidate = await Import(unsupported, ambiguous);
        var json = candidate.DraftFlow.Augments.Single().Settings["flowJson"];
        var draft = NativeFlowDefinition.Read(json, allowBlocked: true);
        Assert.Contains(draft.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains(candidate.DraftFlow.Migration!.Diagnostics, diagnostic => diagnostic.Code == "BT-NATIVE-UNRESOLVED" && diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
        Assert.Throws<NotSupportedException>(() => NativeFlowDefinition.Read(json));
        if (unsupported)
        {
            Assert.Contains(draft.AllNodes(), node => node.Name == "Expression" && node.Kind == NativeFlowNodeKind.Blocked);
            Assert.Contains(draft.AllNodes(), node => node.Name == "global-fact" && node.Kind == NativeFlowNodeKind.Blocked);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DueNativeWaitResumesWithoutUpstreamReplay_AndAmbiguityFreezesFetch(bool failEffect)
    {
        var clock = new Clock(); var source = new Source(); var parses = 0; var effects = 0;
        var checkpoints = new InMemoryFlowOrchestrationStateStore(); var retries = new InMemoryFlowRetryStateStore();
        var catalog = new FakeExecutableModuleCatalog().AddFetch(source)
            .AddParse(new DelegateParseModule("parse", (_, batch, _, _) => { parses++; return Task.FromResult(batch); }))
            .AddDeliver(new DelegateDeliverModule("destination", (_, _, _, _) => { effects++; if (failEffect) throw new IOException("Synthetic ambiguous delivery"); return Task.CompletedTask; }));
        await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddAugment(new NativeControlFlowAugmentModule(new AugmentModuleInvoker(provider), new RenderModuleInvoker(provider), checkpoints, clock, new DeliverModuleInvoker(provider)));
        var definition = new NativeFlowDefinition { Durable = true, Nodes =
            [new() { Kind = NativeFlowNodeKind.SaveMessage, MessageName = "original" }, new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 60 },
             new() { Kind = NativeFlowNodeKind.Deliver, Module = "destination", MessageName = "original" }, new() { Kind = NativeFlowNodeKind.Reply, MessageName = "original" }] };
        var flow = Flow(new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } }, new() { MaxAttempts = 3, Delay = TimeSpan.Zero });
        var runtime = Runtime(flow, catalog, checkpoints, retries, clock);
        Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
        Assert.Empty(source.Outcomes); Assert.Equal(0, effects);
        Assert.Null(await runtime.ReleaseDueBatchAsync(flow.Id, default));
        clock.Now = clock.Now.AddMinutes(1);
        if (failEffect)
        {
            await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Runtime(flow, catalog, checkpoints, retries, clock).ReleaseDueBatchAsync(flow.Id, default));
            Assert.True(Assert.Single(await checkpoints.GetAllAsync(flow.Id, default)).Native!.RecoveryPending);
            Assert.Empty(retries.States); Assert.Empty(source.Outcomes);
            Assert.Null(await runtime.ReleaseDueBatchAsync(flow.Id, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(flow.Id, default));
            await ReconcileViaUi(flow, runtime, catalog, Assert.Single(await checkpoints.GetAllAsync(flow.Id, default)));
            Assert.True(Assert.Single(source.Outcomes).Succeeded); Assert.Empty(await checkpoints.GetAllAsync(flow.Id, default));
        }
        else
        {
            Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(flow, catalog, checkpoints, retries, clock).ReleaseDueBatchAsync(flow.Id, default))!.Outcome);
            Assert.True(Assert.Single(source.Outcomes).Succeeded); Assert.Empty(await checkpoints.GetAllAsync(flow.Id, default));
        }
        Assert.Equal(1, source.Fetches); Assert.Equal(1, parses); Assert.Equal(1, effects);
    }

    [Fact]
    public void NativeDependenciesAndPendingEditsAreCheckedIncludingUnselectedDeliveries()
    {
        var definition = new NativeFlowDefinition { Durable = true, Nodes =
            [new() { Kind = NativeFlowNodeKind.Deliver, Module = "destination", MessageName = "input" }] };
        var flow = Flow(new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } });
        Assert.Contains(("destination", ModuleKind.Deliver), Service.Flows.FlowModuleDependencies.References(flow));
        var mixed = Flow(flow.Augments.Single()); mixed.Augments.Add(new() { Module = "durable-batch-augment" });
        Assert.Throws<ArgumentException>(() => Service.Flows.DurableBatchSafety.Validate(mixed));
        var conditional = Flow(new() { Module = NativeFlowDefinition.ModuleId, Settings = new()
            { ["flowJson"] = JsonSerializer.Serialize(definition, Json), ["conditionJson"] = "{}" } });
        Assert.Throws<ArgumentException>(() => Service.Flows.DurableBatchSafety.Validate(conditional));
        var changed = Flow(new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = "{\"version\":1,\"durable\":true,\"nodes\":[]}" } });
        Assert.Throws<InvalidOperationException>(() => Service.Flows.DurableBatchSafety.EnsureUnchanged(flow, changed, [new() { FlowId = flow.Id, Native = new() }]));
    }

    [Fact]
    public async Task DiskNativeCheckpointPreventsDeleteReplacementAndReferencedConfigEdits()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "pending-state.json");
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [new() { Kind = NativeFlowNodeKind.Render, Module = "renderer",
            Settings = new() { ["path"] = "{{config:map}}" } }] };
        var flow = Flow(new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } });
        var state = new RuntimeMulseState { Pipelines = [flow], OrchestrationCheckpoints = [new() { FlowId = flow.Id, CorrelationKey = "held", Native = new() }],
            ConfigValues = new() { ["map"] = "original" } };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(state, Json));
        var disk = new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = path }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => disk.DeleteFlowAsync(flow.Id, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => disk.DeleteAllFlowsAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => disk.RestoreWorkspaceAsync(new(), true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => disk.SetConfigValueAsync("map", "changed", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => disk.DeleteConfigValueAsync("map", default));
        Assert.Equal("original", disk.GetState().ConfigValues["map"]); Assert.Single(disk.GetState().OrchestrationCheckpoints);
    }

    private static async Task ReconcileViaUi(PipelineDefinition flow, IFlowRuntime runtime, IModuleCatalog catalog, FlowOrchestrationCheckpoint checkpoint)
    {
        var inspectionStore = new FakeRuntimeConfigurationStore(new() { Pipelines = [flow], OrchestrationCheckpoints = [checkpoint] });
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails(); builder.Services.AddExceptionHandler<ApiExceptionHandler>(); ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<IFlowDefinitionService>(new FakeFlowDefinitionService(flow)); builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(inspectionStore); builder.Services.AddSingleton(runtime);
        await using var app = builder.Build(); app.UseExceptionHandler(); app.MapFlowEndpoints(); await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var client = new MulseApiClient(http);
            var inspected = Assert.Single(await client.GetFlowCheckpointsAsync(flow.Id, default));
            Assert.True(inspected.Native); Assert.True(inspected.RecoveryPending); Assert.True(inspected.RequiresEffectOutput); Assert.NotNull(inspected.EffectId);
            var raw = await http.GetStringAsync($"/api/flows/{flow.Id}/checkpoints"); Assert.DoesNotContain("ContentBase64", raw, StringComparison.OrdinalIgnoreCase);
            using var unconfirmed = await http.PostAsJsonAsync($"/api/flows/{flow.Id}/native/{checkpoint.InstanceId}/reconcile", new { ExpectedEffectId = inspected.EffectId, Evidence = "" });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, unconfirmed.StatusCode);
            await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(client).AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<RecoveryHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { ["FlowId"] = flow.Id, ["Checkpoint"] = inspected, ["AllowResume"] = true }))).ToHtmlString());
            Assert.Contains("Native continuation: Completed", html); Assert.DoesNotContain("Native reconciliation failed", html);
            using var stale = await http.PostAsJsonAsync($"/api/flows/{flow.Id}/native/{checkpoint.InstanceId}/reconcile", new { ExpectedEffectId = inspected.EffectId, Evidence = "Verified again", Output = Array.Empty<object>() });
            Assert.Equal(System.Net.HttpStatusCode.NotFound, stale.StatusCode);
        }
        finally { await app.StopAsync(); }
    }
    public sealed class ImportedWaitEditHost : NativeFlowEditor
    {
        protected override async Task OnParametersSetAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var model = (NativeFlowEditorModel)typeof(NativeFlowEditor).GetField("_model", flags)!.GetValue(this)!;
            static IEnumerable<NativeFlowNodeViewModel> All(IEnumerable<NativeFlowNodeViewModel> nodes)
            {
                foreach (var node in nodes)
                {
                    yield return node;
                    foreach (var child in All(node.Children)) yield return child;
                    foreach (var child in All(node.Otherwise)) yield return child;
                }
            }
            var wait = Assert.Single(All(model.Nodes), node => node.Kind == "Wait");
            Assert.Equal(2, wait.DurationSeconds); wait.DurationSeconds = 3;
            await (Task)typeof(NativeFlowEditor).GetMethod("Persist", flags)!.Invoke(this, null)!;
        }
    }

    public sealed class RecoveryHost : NativeEffectRecoveryEditor
    {
        protected override async Task OnParametersSetAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NativeEffectRecoveryEditor).GetField("_verified", flags)!.SetValue(this, true);
            typeof(NativeEffectRecoveryEditor).GetField("_emptyOutput", flags)!.SetValue(this, true);
            typeof(NativeEffectRecoveryEditor).GetField("_evidence", flags)!.SetValue(this, "Synthetic destination independently confirmed committed; no response.");
            await (Task)typeof(NativeEffectRecoveryEditor).GetMethod("ReconcileAsync", flags)!.Invoke(this, null)!;
        }
    }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }

    private static PipelineDefinition Flow(ModuleStepDefinition step, RetryPolicyDefinition? retry = null) => new()
    {
        Id = "native-host", Enabled = true, Retry = retry, Fetches = [new() { Id = "f", Fetch = new() { Module = "source" } }],
        Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }], Augments = [step]
    };
    private static FlowRuntime Runtime(PipelineDefinition flow, IModuleCatalog catalog, IFlowOrchestrationStateStore checkpoints, IFlowRetryStateStore retries, TimeProvider clock)
        => new(new FakeFlowDefinitionService(flow), catalog, checkpoints, retries, new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), clock, NullLogger<FlowRuntime>.Instance);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Source : IFetchOutcomeModule
    {
        public ModuleDescriptor Descriptor { get; } = new("source", "Source", ModuleKind.Fetch, "test");
        public int Fetches { get; private set; }
        public List<FetchProcessingOutcome> Outcomes { get; } = [];
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken token)
        {
            Fetches++;
            return Task.FromResult(new IntegrationBatch([new("input.json", BinaryData.FromString("{\"value\":42}"), "application/json",
                new Dictionary<string, string> { ["group"] = "A", ["messageId"] = "first", ["channel"] = "In" })]) { SourceReceipt = new Dictionary<string, string> { ["id"] = "first" } });
        }
        public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken token)
        { Outcomes.Add(outcome); return Task.CompletedTask; }
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
