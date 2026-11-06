using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Mapping;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;

namespace Service.Tests;

public sealed class NativeNamedRenderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128, Converters = { new JsonStringEnumConverter() } };
    private static NativeFlowNode Render(params string[] inputs) => new() { Id = "map", Kind = NativeFlowNodeKind.Render, Module = "native-map", InputMessages = inputs.ToList(), MessageName = "mapped" };
    private static NativeFlowDefinition Flow(NativeFlowNode render) => new() { Durable = true, Nodes = [
        new() { Kind = NativeFlowNodeKind.Receive, MessageName = "first", Channel = "in" },
        new() { Kind = NativeFlowNodeKind.CreateXmlMessage, MessageName = "second", Expression = new() { Literal = NativeScalar.From("<second/>") } }, render,
        new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 1 }, new() { Kind = NativeFlowNodeKind.Reply, MessageName = render.MessageName }
    ] };
    private static IntegrationBatch Input() => new([new("first.xml", BinaryData.FromString("<first/>"), "application/xml", new Dictionary<string, string> { ["group"] = "A", ["messageId"] = "1", ["channel"] = "in", ["Case"] = "upper", ["case"] = "lower" })]);
    private static IntegrationBatch Output() => new([new("mapped.xml", BinaryData.FromString("<mapped/>"), "application/xml", new Dictionary<string, string> { ["map"] = "configured" })]);
    private static ModuleStepDefinition Step(NativeFlowDefinition flow) => new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(flow, Json) } };
    private static FlowExecutionContext Context(Clock clock, FlowOrchestrationCheckpoint? checkpoint = null)
    {
        var context = new FlowExecutionContext("named-render", Guid.NewGuid().ToString("N"), clock.GetUtcNow());
        if (checkpoint is not null) context.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, checkpoint.Bookmark, true);
        return context;
    }
    private static Task<IntegrationBatch> Run(IFlowOrchestrationStateStore store, Clock clock, Renderer renderer, NativeFlowDefinition flow, FlowExecutionContext context, IntegrationBatch input)
        => new NativeControlFlowAugmentModule(renderer, renderer, store, clock).AugmentAsync(context, input, Step(flow), default);

    [Fact]
    public async Task OrderedRepeatedNamedInputsPreserveWorkingAndOtherMessagesAcrossDiskRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-named-render-" + Guid.NewGuid().ToString("N"));
        try
        {
            var clock = new Clock(); var renderer = new Renderer(); var input = Input(); var flow = Flow(Render("second", "first", "second"));
            IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
            var first = Context(clock);
            await Run(Store(), clock, renderer, flow, first, input);
            Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
            Assert.Equal(new[] { "second.xml", "first.xml", "second.xml" }, Assert.Single(renderer.Inputs).Payloads.Select(payload => payload.Name));
            var saved = Assert.Single(await Store().GetAllAsync("named-render", default));
            Assert.Equal("first.xml", Assert.Single(saved.Payloads).Name);
            Assert.Equal("mapped.xml", Assert.Single(saved.Native!.State.Messages["mapped"]).Name);
            Assert.Equal("upper", Assert.Single(saved.Native.State.Messages["first"]).Metadata["Case"]);
            Assert.Equal("lower", Assert.Single(saved.Native.State.Messages["first"]).Metadata["case"]);
            Assert.Equal("<second/>", Assert.Single(saved.Native.State.Messages["second"]).Restore().GetText());
            clock.Now += TimeSpan.FromSeconds(1);
            var resumed = Context(clock, saved);
            var output = await Run(Store(), clock, renderer, flow, resumed, IntegrationBatch.Empty);
            Assert.Equal("<first/>", Assert.Single(output.Payloads).GetText());
            Assert.Equal("<mapped/>", Assert.Single(resumed.CapturedResponses).GetText());
            Assert.Single(renderer.Inputs);
            Assert.False(input.Payloads[0].Metadata.ContainsKey("map"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NativeNamedRendererInvokesRealCompositionAndMappingPipelineThenRepliesAfterWait()
    {
        var catalog = new FakeExecutableModuleCatalog().AddRender(new XmlPartsRenderModule()).AddRender(new XmlMappingRenderModule());
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
            .AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>().BuildServiceProvider();
        var invoker = services.GetRequiredService<IRenderModuleInvoker>(); catalog.AddRender(new RenderPipelineModule(invoker));
        var mapping = new XmlMappingDefinition { TargetRoot = "Result", Namespaces = new() { ["a"] = "urn:parts" }, Fields = [
            new() { SourcePath = "/a:Root/source/first/Id", TargetPath = "Id" }, new() { SourcePath = "/a:Root/index/second/Count", TargetPath = "Count" }
        ] };
        var node = Render("first", "second"); node.Module = "render-pipeline";
        node.Settings["stagesJson"] = JsonSerializer.Serialize(new[] {
            new { module = "xml-parts-render", settings = new Dictionary<string, string> { ["rootName"] = "Root", ["rootNamespace"] = "urn:parts", ["partNames"] = "source,index", ["outputName"] = "parts.xml" } },
            new { module = "xml-mapping-render", settings = new Dictionary<string, string> { ["mappingJson"] = XmlMappingTests.Json(mapping) } }
        }, Json);
        var flow = Flow(node); flow.Nodes[1].Expression!.Literal = NativeScalar.From("<second><Count>2</Count></second>");
        var input = new IntegrationBatch([Input().Payloads[0] with { Content = BinaryData.FromString("<first><Id>42</Id></first>") }]);
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock();
        var module = new NativeControlFlowAugmentModule(new Renderer(), invoker, store, clock);
        await module.AugmentAsync(Context(clock), input, Step(flow), default);
        clock.Now += TimeSpan.FromSeconds(1);
        var resumed = Context(clock, store.Checkpoints.Single());
        var working = await module.AugmentAsync(resumed, IntegrationBatch.Empty, Step(flow), default);
        Assert.Equal(input.Payloads[0].GetText(), Assert.Single(working.Payloads).GetText());
        var response = Assert.Single(resumed.CapturedResponses); var result = System.Xml.Linq.XDocument.Parse(response.GetText()).Root!;
        Assert.Equal("42", result.Element("Id")!.Value); Assert.Equal("2", result.Element("Count")!.Value);
        Assert.False(response.Metadata.ContainsKey("group")); Assert.False(response.Metadata.ContainsKey("messageId"));
    }

    [Theory]
    [InlineData("first")]
    [InlineData("second")]
    public async Task OutputMayReplaceAnInputOnlyAfterTheRendererReturns(string destination)
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var renderer = new Renderer();
        var node = Render("first", "second"); node.MessageName = destination;
        await Run(store, clock, renderer, Flow(node), Context(clock), Input());
        Assert.Equal("<first/>", renderer.Inputs.Single().Payloads[0].GetText());
        Assert.Equal("<second/>", renderer.Inputs.Single().Payloads[1].GetText());
        Assert.Equal("<mapped/>", store.Checkpoints.Single().Native!.State.Messages[destination].Single().Restore().GetText());
        Assert.Equal("<first/>", store.Checkpoints.Single().Payloads.Single().Restore().GetText());
    }

    [Fact]
    public async Task MissingNamedInputFailsBeforeRendererIntentOrInvocation()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var renderer = new Renderer();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, renderer, Flow(Render("first", "missing")), Context(clock), Input()));
        Assert.Empty(renderer.Inputs);
        var state = store.Checkpoints.Single().Native!.State;
        Assert.Null(state.EffectNodeId); Assert.False(state.Messages.ContainsKey("mapped")); Assert.Equal("Faulted", state.Status);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task NamedInputMustBeOnePayloadBeforeTheNamedRendererIsInvoked(int count)
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock();
        var renderer = new Renderer { Result = new(Enumerable.Repeat(Output().Payloads[0], count).ToArray()) };
        var flow = Flow(Render("first", "batch"));
        flow.Nodes.Insert(2, new() { Kind = NativeFlowNodeKind.Render, Module = "expand" });
        flow.Nodes.Insert(3, new() { Kind = NativeFlowNodeKind.SaveMessage, MessageName = "batch" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, renderer, flow, Context(clock), Input()));
        Assert.Single(renderer.Inputs);
        var state = store.Checkpoints.Single().Native!.State;
        Assert.Null(state.EffectNodeId); Assert.False(state.Messages.ContainsKey("mapped"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task InvalidRendererCardinalityHoldsIntentAndCannotBeReplayed(int count)
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var renderer = new Renderer { Result = new(Enumerable.Repeat(Output().Payloads[0], count).ToArray()) };
        var flow = Flow(Render("first", "second"));
        await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(store, clock, renderer, flow, Context(clock), Input()));
        var saved = store.Checkpoints.Single();
        Assert.Equal("map", saved.Native!.State.EffectNodeId); Assert.False(saved.Native.State.Messages.ContainsKey("mapped"));
        await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(store, clock, renderer, flow, Context(clock, saved), IntegrationBatch.Empty));
        Assert.Single(renderer.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOutcomeAcknowledgementNeverReplaysTheNamedRenderer(bool committed)
    {
        var store = new OutcomeStore(committed); var clock = new Clock(); var renderer = new Renderer(); var flow = Flow(Render("first", "second"));
        await Assert.ThrowsAsync<NativePersistenceException>(() => Run(store, clock, renderer, flow, Context(clock), Input()));
        var saved = Assert.Single(await store.GetAllAsync("named-render", default));
        if (!committed)
        {
            Assert.False(saved.Native!.State.Messages.ContainsKey("mapped"));
            await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(store, clock, renderer, flow, Context(clock, saved), IntegrationBatch.Empty));
            await new NativeDurableRecovery(store, clock).ConfirmCommittedAsync(saved.FlowId, saved.CorrelationKey, flow, "map", "synthetic verified persisted output", Output(), default);
        }
        else Assert.Equal("<mapped/>", saved.Native!.State.Messages["mapped"].Single().Restore().GetText());
        saved = Assert.Single(await store.GetAllAsync("named-render", default));
        await Run(store, clock, renderer, flow, Context(clock, saved), IntegrationBatch.Empty);
        clock.Now += TimeSpan.FromSeconds(1);
        saved = Assert.Single(await store.GetAllAsync("named-render", default));
        var resumed = Context(clock, saved); await Run(store, clock, renderer, flow, resumed, IntegrationBatch.Empty);
        Assert.Equal("<mapped/>", Assert.Single(resumed.CapturedResponses).GetText()); Assert.Single(renderer.Inputs);
    }

    [Fact]
    public async Task VerifiedCommitWritesOnlyTheNamedOutputAndResumesWithoutRepeatingRenderer()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var renderer = new Renderer { Fail = true };
        var flow = Flow(Render("second", "first"));
        await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(store, clock, renderer, flow, Context(clock), Input()));
        var saved = store.Checkpoints.Single(); var recovery = new NativeDurableRecovery(store, clock);
        var before = JsonSerializer.Serialize(saved, Json);
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.ConfirmCommittedAsync(saved.FlowId, saved.CorrelationKey, flow, "map", "synthetic verified output", IntegrationBatch.Empty, default));
        Assert.Equal(before, JsonSerializer.Serialize(store.Checkpoints.Single(), Json));
        await recovery.ConfirmCommittedAsync(saved.FlowId, saved.CorrelationKey, flow, "map", "synthetic verified output", Output(), default);
        var reconciled = store.Checkpoints.Single();
        Assert.Equal("<first/>", reconciled.Payloads.Single().Restore().GetText());
        Assert.Equal("<mapped/>", reconciled.Native!.State.Messages["mapped"].Single().Restore().GetText());
        Assert.Single(reconciled.Native.State.CommittedEffects);
        await Run(store, clock, renderer, flow, Context(clock, reconciled), IntegrationBatch.Empty);
        clock.Now += TimeSpan.FromSeconds(1);
        var resumed = Context(clock, store.Checkpoints.Single());
        await Run(store, clock, renderer, flow, resumed, IntegrationBatch.Empty);
        Assert.Equal("<mapped/>", Assert.Single(resumed.CapturedResponses).GetText()); Assert.Single(renderer.Inputs);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("too-many")]
    [InlineData("blank")]
    [InlineData("non-durable")]
    [InlineData("inactive")]
    [InlineData("no-output")]
    [InlineData("no-inputs")]
    public void InvalidNamedRendererContractsFailClosed(string variation)
    {
        var node = Render("first"); var flow = Flow(node);
        switch (variation)
        {
            case "empty": node.InputMessages = []; break;
            case "too-many": node.InputMessages = Enumerable.Repeat("first", 33).ToList(); break;
            case "blank": node.InputMessages = [" "]; break;
            case "non-durable": flow.Durable = false; break;
            case "inactive": node.Kind = NativeFlowNodeKind.Deliver; break;
            case "no-output": node.MessageName = ""; break;
            case "no-inputs": node.InputMessages = null; break;
        }
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(flow, Json)));
    }

    [Fact]
    public void NamedRendererInputBoundsAreExactAndPreserveRepeatedArgumentOrder()
    {
        var node = Render(Enumerable.Repeat(new string('m', 128), 32).ToArray()); node.MessageName = new string('o', 128);
        NativeFlowDefinition.Read(JsonSerializer.Serialize(Flow(node), Json));
        node.InputMessages![0] += "x";
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(Flow(node), Json)));
        node.InputMessages[0] = "first"; node.MessageName += "x";
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(Flow(node), Json)));
    }

    [Fact]
    public void OldRenderDefinitionsRemainUnchangedAndDependenciesIncludeNamedRenderer()
    {
        var ordinary = new NativeFlowDefinition { Nodes = [new() { Kind = NativeFlowNodeKind.Render, Module = "native-map" }] };
        var json = JsonSerializer.Serialize(ordinary, Json); Assert.DoesNotContain("inputMessages", json);
        NativeFlowDefinition.Read(json);
        var definition = Flow(Render("first", "second"));
        Assert.Contains(("native-map", ModuleKind.Render), FlowModuleDependencies.References(new() { Id = "named-render", Augments = [Step(definition)] }));
    }

    [Fact]
    public async Task NamedRendererCanBeGraphicallyConfiguredReorderedReopenedAndReset()
    {
        var model = new NativeFlowEditorModel { Durable = true, Nodes = [new() { Kind = "Render", Module = "native-map" }] };
        var node = model.Nodes.Single(); var editor = new NativeFlowNodesEditor();
        typeof(NativeFlowNodesEditor).GetProperty(nameof(editor.Nodes))!.SetValue(editor, model.Nodes);
        await Invoke(editor, "ChangeRenderMode", node, "Named");
        await Invoke(editor, "ChangeRenderInput", node, 0, "first");
        await Invoke(editor, "AddRenderInput", node); await Invoke(editor, "ChangeRenderInput", node, 1, "second");
        await Invoke(editor, "MoveRenderInput", node, 1, -1); node.MessageName = "mapped";
        var json = JsonSerializer.Serialize(model, Json);
        Assert.Equal(new[] { "second", "first" }, NativeFlowDefinition.Read(json).Nodes.Single().InputMessages);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var view = await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = json, ["Modules"] = new ModuleViewModel[] { new("native-map", "Native map", "Render", "Synthetic", [], [], [], []) } }));
            Assert.Contains("Ordered named inputs to named output", view.ToHtmlString()); Assert.Contains("Input argument 2", view.ToHtmlString());
            Assert.DoesNotContain("Some nodes are incomplete", view.ToHtmlString()); Assert.DoesNotContain("Original JSON is preserved", view.ToHtmlString());
        });
        await Invoke(editor, "RemoveRenderInput", node, 0); Assert.Equal("first", Assert.Single(node.InputMessages!));
        await Invoke(editor, "ChangeRenderMode", node, "Working"); Assert.Null(node.InputMessages); Assert.Equal("", node.MessageName);
        await Invoke(editor, "ChangeRenderMode", node, "Named"); await Invoke(editor, "ChangeKind", node, "Sequence"); Assert.Null(node.InputMessages);
    }

    private static async Task Invoke(object instance, string method, params object?[] args) => await (Task)instance.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(instance, args)!;
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class OutcomeStore(bool committed) : IFlowOrchestrationStateStore
    {
        private readonly InMemoryFlowOrchestrationStateStore _inner = new();
        private bool _failed;
        public Task<FlowOrchestrationCheckpoint?> GetAsync(string flow, string key, CancellationToken token) => _inner.GetAsync(flow, key, token);
        public async Task UpsertAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken token)
        {
            var loseOutcome = !_failed && checkpoint.Native?.State.EffectNodeId is null && (await GetAsync(checkpoint.FlowId, checkpoint.CorrelationKey, token))?.Native?.State.EffectNodeId == "map";
            if (loseOutcome && !committed) { _failed = true; throw new IOException("Synthetic outcome not committed."); }
            await _inner.UpsertAsync(checkpoint, token);
            if (loseOutcome) { _failed = true; throw new IOException("Synthetic outcome acknowledgement lost."); }
        }
        public Task DeleteAsync(string flow, string key, CancellationToken token) => _inner.DeleteAsync(flow, key, token);
        public Task<IReadOnlyList<FlowOrchestrationCheckpoint>> GetAllAsync(string flow, CancellationToken token) => _inner.GetAllAsync(flow, token);
        public Task<IReadOnlyList<FetchProcessingReceipt>> GetPendingSourceReceiptsAsync(string flow, CancellationToken token) => _inner.GetPendingSourceReceiptsAsync(flow, token);
    }
    private sealed class Renderer : IRenderModuleInvoker, IAugmentModuleInvoker
    {
        public List<IntegrationBatch> Inputs { get; } = [];
        public IntegrationBatch Result { get; set; } = Output();
        public bool Fail { get; set; }
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        {
            Inputs.Add(batch);
            if (Fail) throw new IOException("Synthetic ambiguous renderer outcome.");
            return Task.FromResult(Result);
        }
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No augment expected.");
    }
}
