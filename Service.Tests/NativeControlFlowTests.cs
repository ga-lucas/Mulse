using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Service.Execution;
using Service.Modules;

namespace Service.Tests;

public sealed class NativeControlFlowTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128, Converters = { new JsonStringEnumConverter() } };
    private static string Serialize(NativeFlowDefinition flow) => JsonSerializer.Serialize(flow, Json);
    private static NativeFlowNode RenderNode(string module) => new() { Kind = NativeFlowNodeKind.Render, Module = module };
    private static DecisionConditionDefinition Condition(string value) => new() { Source = DecisionValueSourceKind.Metadata, Path = "kind", Operator = DecisionComparisonOperator.Equals, Value = value };
    private static IntegrationPayload Payload(string name, string kind) => new(name, BinaryData.FromString("{}"), "application/json", new Dictionary<string, string> { ["kind"] = kind });
    private static ModuleStepDefinition Step(NativeFlowDefinition flow) => new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = Serialize(flow) } };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScratchNativeFlowExecutesBothBranchesInOrderWithNoOdxAndLeasedModules(bool missingElseModule)
    {
        var calls = new List<string>(); var delivered = new List<IntegrationBatch>(); var fetched = false;
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("input", (_, _, _, _) =>
            {
                fetched = true; return Task.FromResult(new IntegrationBatch(missingElseModule ? [Payload("one", "A")] : [Payload("one", "A"), Payload("two", "B")]));
            }))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(new DelegateRenderModule("a", (_, batch, _, _) => { calls.Add("a:" + batch.Payloads.Single().Name); return Task.FromResult(batch); }))
            .AddRender(new DelegateRenderModule("output", (_, batch, _, _) => Task.FromResult(batch)))
            .AddDeliver(DelegateModuleFactory.Deliver("destination", delivered));
        if (!missingElseModule) catalog.AddRender(new DelegateRenderModule("b", (_, batch, _, _) => { calls.Add("b:" + batch.Payloads.Single().Name); return Task.FromResult(batch); }));
        await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddAugment(new NativeControlFlowAugmentModule(new AugmentModuleInvoker(provider), new RenderModuleInvoker(provider)));
        var definition = new NativeFlowDefinition { Nodes = [new() { Kind = NativeFlowNodeKind.ForEach, MaxIterations = 2, Children =
            [new() { Kind = NativeFlowNodeKind.If, Conditions = [Condition("A")], Children = [RenderNode("a")], Otherwise = [RenderNode("b")] }] }] };
        var flow = new PipelineDefinition { Id = "native", Fetches = [new() { Id = "f", Fetch = new() { Module = "input" } }],
            Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }], Augments = [Step(definition)],
            Deliveries = [new() { Render = new() { Module = "output" }, Delivers = [new() { Deliver = new() { Module = "destination" } }] }] };
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, new InMemoryFlowOrchestrationStateStore(), new InMemoryFlowRetryStateStore(),
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
        if (missingElseModule)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(flow.Id, default));
            Assert.False(fetched); Assert.Empty(calls); Assert.Empty(delivered);
            return;
        }
        Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
        Assert.True(fetched);
        Assert.Equal(new[] { "a:one", "b:two" }, calls);
        Assert.Equal(new[] { "one", "two" }, delivered.Single().Payloads.Select(payload => payload.Name));
        Assert.DoesNotContain("Odx", Serialize(definition));
    }

    [Fact]
    public async Task NativeSequenceRunsRealRulesThenDynamicMapsWithoutChangingInputOrdering()
    {
        var calls = new List<string>();
        var catalog = new FakeExecutableModuleCatalog().AddAugment(new XmlRulesAugmentModule())
            .AddRender(new DelegateRenderModule("a", (_, batch, _, _) => { calls.Add("a"); return Task.FromResult(batch); }))
            .AddRender(new DelegateRenderModule("b", (_, batch, _, _) => { calls.Add("b"); return Task.FromResult(batch); }));
        await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddAugment(new MapDispatchAugmentModule(new RenderModuleInvoker(provider)));
        var policy = new XmlRulePolicy { Name = "Synthetic routing", Version = "1.0", RootName = "Facts", Rules =
            [new() { Name = "A", Conditions = [new() { Name = "kind", Value = "A" }], Assignments = [new() { Name = "MapName", Value = "a" }] },
             new() { Name = "B", Conditions = [new() { Name = "kind", Value = "B" }], Assignments = [new() { Name = "MapName", Value = "b" }] }] };
        var native = new NativeFlowDefinition { Nodes = [new() { Kind = NativeFlowNodeKind.Sequence, Children =
            [new() { Kind = NativeFlowNodeKind.Augment, Module = "xml-rules-augment", Settings = new() { ["policyJson"] = JsonSerializer.Serialize(policy, Json), ["factMode"] = "Metadata", ["metadataPrefix"] = "" } },
             new() { Kind = NativeFlowNodeKind.Augment, Module = "map-dispatch-augment", Settings = new() { ["metadataKey"] = "MapName", ["casesJson"] = "[{\"value\":\"a\",\"module\":\"a\",\"settings\":{}},{\"value\":\"b\",\"module\":\"b\",\"settings\":{}}]" } }] }] };
        var flow = new PipelineDefinition { Id = "native", Augments = [Step(native)] };
        var resolved = new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())).Resolve(flow);
        Assert.Empty(Service.Flows.FlowModuleDependencies.FindMissing(resolved, catalog.AddAugment(new NativeControlFlowAugmentModule(new AugmentModuleInvoker(provider), new RenderModuleInvoker(provider))).GetAll()));
        var output = await new NativeControlFlowAugmentModule(new AugmentModuleInvoker(provider), new RenderModuleInvoker(provider)).AugmentAsync(new("native", "run", DateTimeOffset.UtcNow),
            new([Payload("one", "A"), Payload("two", "B")]), resolved.Augments.Single(), default);
        Assert.Equal(new[] { "a", "b" }, calls); Assert.Equal(new[] { "one", "two" }, output.Payloads.Select(payload => payload.Name));
        Assert.All(output.Payloads, payload => Assert.Equal("{}", payload.GetText()));
    }

    [Theory]
    [InlineData(2000, false)]
    [InlineData(2001, true)]
    public void ExactNodeCountLimitsAreEnforced(int count, bool rejected)
    {
        var json = Serialize(new() { Nodes = Enumerable.Range(0, count).Select(_ => new NativeFlowNode()).ToList() });
        if (rejected) Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(json));
        else Assert.Equal(count, NativeFlowDefinition.Read(json).AllNodes().Count());
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void NativeDefinitionAndReferenceResolverPreserveExactNestingLimit(int depth, bool rejected)
    {
        var root = new NativeFlowNode(); var current = root;
        for (var index = 0; index < depth; index++) { var child = new NativeFlowNode(); current.Children.Add(child); current = child; }
        var flow = new PipelineDefinition { Id = "native", Augments = [Step(new() { Nodes = [root] })] };
        var resolved = new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())).Resolve(flow);
        if (rejected) Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(resolved.Augments.Single().Settings["flowJson"]));
        else Assert.Equal(depth + 1, NativeFlowDefinition.Read(resolved.Augments.Single().Settings["flowJson"]).AllNodes().Count());
    }

    [Fact]
    public async Task WhileTestsUpdatedOutputAndStopsAtExactLimit()
    {
        var invoker = new Invoker(); var module = new NativeControlFlowAugmentModule(invoker, invoker);
        var loop = new NativeFlowNode { Kind = NativeFlowNodeKind.While, MaxIterations = 1, Conditions = [Condition("A")], Children = [RenderNode("change")] };
        var result = await module.AugmentAsync(new("native", "run", DateTimeOffset.UtcNow), new([Payload("one", "A")]), Step(new() { Nodes = [loop] }), default);
        Assert.Equal("B", result.Payloads.Single().Metadata["kind"]); Assert.Equal(new[] { "change" }, invoker.Calls);
        loop.Children = [RenderNode("unchanged")]; invoker.Calls.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.AugmentAsync(new("native", "run", DateTimeOffset.UtcNow), new([Payload("one", "A")]), Step(new() { Nodes = [loop] }), default));
        Assert.Single(invoker.Calls);
    }

    [Fact]
    public async Task WholeDefinitionPreflightRejectsUnreachableBlockerBeforeAnyModule()
    {
        var invoker = new Invoker(); var module = new NativeControlFlowAugmentModule(invoker, invoker);
        var definition = new NativeFlowDefinition { Nodes = [RenderNode("first"), new() { Kind = NativeFlowNodeKind.If, Conditions = [Condition("A")], Otherwise =
            [new() { Kind = NativeFlowNodeKind.Blocked, BlockReason = "untranslated custom component" }] }] };
        Assert.Equal(3, NativeFlowDefinition.Read(Serialize(definition), allowBlocked: true).AllNodes().Count());
        await Assert.ThrowsAsync<NotSupportedException>(() => module.AugmentAsync(new("native", "run", DateTimeOffset.UtcNow), new([Payload("one", "A")]), Step(definition), default));
        Assert.Empty(invoker.Calls);
        var missing = Service.Flows.FlowModuleDependencies.FindMissing(new() { Id = "native", Augments = [Step(definition)] }, []);
        Assert.Contains("untranslated custom component", missing.Single());
    }

    [Fact]
    public async Task ModuleFailureAndCancellationStopLaterNodes()
    {
        var invoker = new Invoker(); var module = new NativeControlFlowAugmentModule(invoker, invoker);
        var step = Step(new() { Nodes = [RenderNode("fail"), RenderNode("later")] });
        await Assert.ThrowsAsync<IOException>(() => module.AugmentAsync(new("n", "r", DateTimeOffset.UtcNow), new([Payload("one", "A")]), step, default));
        Assert.Equal(new[] { "fail" }, invoker.Calls);
        invoker.Calls.Clear();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.AugmentAsync(new("n", "r", DateTimeOffset.UtcNow), new([Payload("one", "A")]), step, cancellation.Token));
        Assert.Empty(invoker.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    public void InvalidLoopLimitsAreRejected(int limit)
    {
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Serialize(new() { Nodes = [new() { Kind = NativeFlowNodeKind.ForEach, MaxIterations = limit }] })));
    }

    [Fact]
    public void DuplicateIdsUnknownKindsStatefulModulesAndInactiveBranchesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read("{\"version\":1,\"nodes\":[{\"id\":\"x\"},{\"id\":\"x\"}]}"));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read("{\"version\":1,\"nodes\":[{\"kind\":\"Receive\"}]}"));
        foreach (var id in new[] { "odx-program-augment", NativeFlowDefinition.ModuleId, "durable-batch-augment", "stateful-orchestration-augment" })
            Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Serialize(new() { Nodes = [new() { Kind = NativeFlowNodeKind.Augment, Module = id }] })));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Serialize(new() { Nodes = [new() { Otherwise = [RenderNode("hidden")] }] })));
    }

    [Fact]
    public async Task ForeachChecksLimitBeforeFirstIterationAndConditionRequiresSinglePayload()
    {
        var invoker = new Invoker(); var module = new NativeControlFlowAugmentModule(invoker, invoker);
        var batch = new IntegrationBatch([Payload("one", "A"), Payload("two", "B")]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.AugmentAsync(new("n", "r", DateTimeOffset.UtcNow), batch,
            Step(new() { Nodes = [new() { Kind = NativeFlowNodeKind.ForEach, MaxIterations = 1, Children = [RenderNode("first")] }] }), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.AugmentAsync(new("n", "r", DateTimeOffset.UtcNow), batch,
            Step(new() { Nodes = [new() { Kind = NativeFlowNodeKind.If, Conditions = [Condition("A")] }] }), default));
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public void NestedModuleReferencesIncludeBothBranchesAndStoredSettingsAreEscaped()
    {
        var flow = new PipelineDefinition { Id = "n", Augments = [Step(new() { Nodes = [new() { Kind = NativeFlowNodeKind.If, Conditions = [Condition("A")],
            Children = [RenderNode("a")], Otherwise = [new() { Kind = NativeFlowNodeKind.Augment, Module = "decision-augment", Settings = new() { ["option"] = "{{config:value}}" } }] }] })] };
        var literal = "C:\\path\\\"quoted\"";
        var resolved = new ConfigValueResolver(new FakeRuntimeConfigurationStore(new() { ConfigValues = { ["value"] = literal } })).Resolve(flow);
        Assert.Equal(literal, NativeFlowDefinition.Read(resolved.Augments[0].Settings["flowJson"]).AllNodes().Single(node => node.Kind == NativeFlowNodeKind.Augment).Settings["option"]);
        var references = Service.Flows.FlowModuleDependencies.References(resolved).ToArray();
        Assert.Contains(("a", ModuleKind.Render), references); Assert.Contains(("decision-augment", ModuleKind.OrchestrationAugment), references);
    }

    [Fact]
    public async Task VisualAuthoringSavesReordersAndReopensExecutableNativeDefinition()
    {
        var model = new NativeFlowEditorModel(); var editor = new NativeFlowNodesEditor();
        Set(editor, nameof(editor.Nodes), model.Nodes);
        Set(editor, nameof(editor.Modules), new ModuleViewModel[] { new("a", "Renderer A", "Render", "Synthetic", [], [], [], []) });
        string saved = "";
        Set(editor, nameof(editor.Changed), EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(editor, "Add"); var node = model.Nodes.Single();
        await Invoke(editor, "ChangeKind", node, "ForEach");
        var body = new NativeFlowNodesEditor();
        Set(body, nameof(body.Nodes), node.Children); Set(body, nameof(body.Modules), editor.Modules); Set(body, nameof(body.Changed), editor.Changed);
        await Invoke(body, "Add"); var first = node.Children.Single();
        await Invoke(body, "ChangeKind", first, "Render"); await Invoke(body, "ChangeModule", first, "a");
        await Invoke(body, "Add"); var second = node.Children.Last();
        await Invoke(body, "Move", second, -1);
        Assert.Equal(second.Id, NativeFlowDefinition.Read(saved).Nodes.Single().Children.First().Id);
        var rendered = await Render(saved);
        Assert.Contains("ForEach", rendered); Assert.Contains("Maximum iterations", rendered); Assert.Contains("Renderer A", rendered);
        Assert.Contains("Move node earlier", rendered); Assert.DoesNotContain("Source program", rendered);
        await Invoke(body, "Remove", second); Assert.Single(NativeFlowDefinition.Read(saved).Nodes.Single().Children);
        var invoker = new Invoker();
        Assert.Single((await new NativeControlFlowAugmentModule(invoker, invoker).AugmentAsync(new("n", "r", DateTimeOffset.UtcNow), new([Payload("one", "A")]),
            new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = saved } }, default)).Payloads);
        Assert.Equal(new[] { "a" }, invoker.Calls);
    }

    [Theory]
    [InlineData("{\"version\":1,\"nodes\":[null]}")]
    [InlineData("not-json")]
    [InlineData("{\"version\":1,\"nodes\":[{\"kind\":\"Unknown\"}]}")]
    [InlineData("{\"version\":1,\"nodes\":[{\"kind\":\"Sequence\"}]}")]
    [InlineData("{\"nodes\":[]}")]
    public async Task MalformedVisualDefinitionsPreserveOriginalJson(string json)
    {
        var html = await Render(json);
        Assert.Contains("Original JSON is preserved", html); Assert.DoesNotContain("Add native node", html);
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(json));
    }

    private static void Set(object instance, string name, object value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static async Task Invoke(object instance, string method, params object?[] arguments)
        => await (Task)instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments)!;
    private static async Task<string> Render(string json)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
        { ["Value"] = json, ["Modules"] = new ModuleViewModel[] { new("a", "Renderer A", "Render", "Synthetic", [], [], [], []) } }))).ToHtmlString());
    }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
    private sealed class Invoker : Mulse.Modules.Abstractions.IAugmentModuleInvoker, Mulse.Modules.Abstractions.IRenderModuleInvoker
    {
        public List<string> Calls { get; } = [];
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
            => RenderAsync(id, context, batch, settings, token);
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        {
            Calls.Add(id); if (id == "fail") throw new IOException("Synthetic failure");
            return Task.FromResult(id == "change" ? new IntegrationBatch(batch.Payloads.Select(payload => payload with
                { Metadata = new Dictionary<string, string>(payload.Metadata) { ["kind"] = "B" } }).ToArray()) : batch);
        }
    }
}
