using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.ControlFlow;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class NativeFlowVisualEditorTests
{
    private static NativeFlowEditorModel Definition() => new()
    {
        Durable = true,
        Nodes =
        [
            new() { Id = "choice", Name = "Choose route", Kind = "If", Conditions = [new() { Path = "kind", Value = "order" }],
                Children = [new() { Id = "first", Name = "First wait", Kind = "Wait", DurationSeconds = 5 }, new() { Id = "second", Name = "Second wait", Kind = "Wait", DurationSeconds = 10 }],
                Otherwise = [new() { Id = "receive", Name = "Receive fallback", Kind = "Receive", Channel = "fallback", MessageName = "request" }] },
            new() { Id = "loop", Name = "Each payload", Kind = "ForEach", Children = [new() { Id = "augment", Name = "Enrich", Kind = "Augment", Module = "enrich", Settings = new() { ["token"] = "private-setting-not-a-card-label" } }] },
            new() { Id = "try", Kind = "Try", Otherwise = [new() { Id = "blocker", Name = "Unresolved operation", Kind = "Blocked", BlockReason = "Needs implementation" }] }
        ]
    };
    private static readonly ModuleViewModel[] Modules =
    [
        new("native-control-flow-augment", "Native control flow", "OrchestrationAugment", "Native steps", [new("flowJson", "Native definition", "", true, "Text", "{\"version\":1,\"nodes\":[]}", [])], [], [], []),
        new("enrich", "Enrichment module", "OrchestrationAugment", "Enrich", [new("token", "Token", "", false, "Password", "", [])], [], [], [])
    ];

    [Fact]
    public async Task GraphShowsEveryBranchButOnlySelectedStepSettingsAndBlockersRemainEditable()
    {
        var activator = new TrackingActivator();
        await using var services = Services(activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<NativeFlowEditor>(Parameters(Definition()));
            var editor = Assert.Single(activator.Created.OfType<NativeFlowEditor>());
            var graph = activator.Created.OfType<NativeFlowGraph>().First();
            var html = rendered.ToHtmlString();
            Assert.Equal(8, Count(html, "data-native-node-id="));
            Assert.Equal(1, Count(html, "Node name <input"));
            Assert.Contains("Then", html); Assert.Contains("Else", html); Assert.Contains("Try body", html); Assert.Contains("Catch handler", html);
            Assert.Contains("Repeat for each input payload", html);
            Assert.Contains("Enrichment module", html);
            Assert.Contains("Unresolved: Needs implementation", html);
            Assert.Contains("Predicate mode", html);
            Assert.DoesNotContain("Wait seconds", html);
            Assert.DoesNotContain("private-setting-not-a-card-label", html[..html.IndexOf("Selected augment step properties", StringComparison.Ordinal)]);
            await graph.Selected.InvokeAsync("second");
            html = rendered.ToHtmlString();
            Assert.Contains("Wait seconds", html); Assert.Contains("value=\"10\"", html);
            Assert.DoesNotContain("Predicate mode", html); Assert.DoesNotContain("Incoming channel", html);
            Assert.Equal(1, Count(html, "Node name <input"));
            Assert.Equal(8, Count(html, "data-native-node-id="));
            Assert.Matches("aria-pressed=\"true\"[^>]*data-native-node-id=\"second\"", html);
            Assert.DoesNotContain("aria-controls=\"PropertiesId\"", html);
            var blockerLink = System.Text.RegularExpressions.Regex.Match(html, "href=\"#(?<id>native-node-[a-f0-9]+-blocker)\"");
            Assert.True(blockerLink.Success);
            Assert.Contains("id=\"" + blockerLink.Groups["id"].Value + "\"", html);
            var saved = editor.Value;
            await graph.Selected.InvokeAsync("blocker");
            Assert.Contains("Blocking reason", rendered.ToHtmlString());
            Assert.Equal(saved, editor.Value);
            await editor.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("Blocking reason", rendered.ToHtmlString());
            Assert.Equal("blocker", Field(editor, "_selectedNodeId"));
        });
    }

    [Fact]
    public async Task GraphCallbacksPreserveScopeIdsAndSettingsThroughInsertMoveAndConfirmedSubtreeRemoval()
    {
        var activator = new TrackingActivator();
        await using var services = Services(activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            string saved = "";
            var parameters = new Dictionary<string, object?>(Parameters(Definition()).ToDictionary());
            parameters[nameof(NativeFlowEditor.ValueChanged)] = EventCallback.Factory.Create<string>(new object(), value => saved = value);
            var rendered = await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(parameters));
            var editor = Assert.Single(activator.Created.OfType<NativeFlowEditor>());
            var graph = activator.Created.OfType<NativeFlowGraph>().First();
            await graph.Selected.InvokeAsync("second");
            await (Task)Invoke(editor, "MoveGraphNode", -1)!;
            var read = JsonSerializer.Deserialize<NativeFlowEditorModel>(saved, JsonSerializerOptions.Web)!;
            Assert.Equal(["second", "first"], read.Nodes[0].Children.Select(node => node.Id));
            Assert.Equal("receive", Assert.Single(read.Nodes[0].Otherwise).Id);
            Assert.Equal(10, read.Nodes[0].Children[0].DurationSeconds);
            var model = (NativeFlowEditorModel)Field(editor, "_model")!;
            var alternative = activator.Created.OfType<NativeFlowGraph>().Single(component => ReferenceEquals(component.Nodes, model.Nodes[0].Otherwise));
            await alternative.AddRequested.InvokeAsync(alternative.Nodes);
            var addedId = (string)Field(editor, "_selectedNodeId")!;
            read = JsonSerializer.Deserialize<NativeFlowEditorModel>(saved, JsonSerializerOptions.Web)!;
            Assert.Equal(["receive", addedId], read.Nodes[0].Otherwise.Select(node => node.Id));
            Assert.Equal("Sequence", read.Nodes[0].Otherwise[1].Kind);
            await graph.Selected.InvokeAsync("loop");
            var before = editor.Value;
            await (Task)Invoke(editor, "RemoveGraphNode")!;
            Assert.Equal(before, editor.Value);
            SetField(editor, "_removeNodeId", "loop");
            await editor.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("1 child step(s)", rendered.ToHtmlString());
            await (Task)Invoke(editor, "RemoveGraphNode")!;
            read = JsonSerializer.Deserialize<NativeFlowEditorModel>(saved, JsonSerializerOptions.Web)!;
            Assert.Equal(["choice", "try"], read.Nodes.Select(node => node.Id));
            Assert.Equal(["second", "first"], read.Nodes[0].Children.Select(node => node.Id));
            Assert.Equal("try", Field(editor, "_selectedNodeId"));
            await editor.SetParametersAsync(ParameterView.Empty);
            Assert.DoesNotContain("data-native-node-id=\"augment\"", rendered.ToHtmlString());
            Assert.Contains("data-native-node-id=\"blocker\"", rendered.ToHtmlString());
            var reopened = await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = saved, ["Modules"] = Modules, ["ShowGraph"] = true }));
            Assert.Equal(7, Count(reopened.ToHtmlString(), "data-native-node-id="));
        });
    }

    [Theory]
    [InlineData("stale-scope")]
    [InlineData("list-limit")]
    [InlineData("total-limit")]
    [InlineData("depth-limit")]
    public async Task GraphInsertionRefusesStaleScopesAndExactDefinitionLimits(string scenario)
    {
        var model = new NativeFlowEditorModel();
        List<NativeFlowNodeViewModel> target = model.Nodes;
        if (scenario == "stale-scope") target = [];
        else if (scenario == "list-limit") for (var index = 0; index < 1000; index++) model.Nodes.Add(new());
        else if (scenario == "total-limit")
        {
            model.Nodes = [new() { Children = Enumerable.Range(0, 999).Select(_ => new NativeFlowNodeViewModel()).ToList() }, new() { Children = Enumerable.Range(0, 999).Select(_ => new NativeFlowNodeViewModel()).ToList() }];
            target = model.Nodes;
        }
        else for (var depth = 0; depth < 33; depth++) { var node = new NativeFlowNodeViewModel(); target.Add(node); target = node.Children; }
        var editor = new NativeFlowEditor();
        Set(editor, nameof(editor.Value), JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = 128 }));
        Invoke(editor, "OnParametersSet");
        Assert.False((bool)Field(editor, "_loadFailed")!);
        var loaded = (NativeFlowEditorModel)Field(editor, "_model")!;
        if (scenario is "list-limit" or "total-limit") target = loaded.Nodes;
        if (scenario == "depth-limit") { target = loaded.Nodes; for (var depth = 0; depth < 33; depth++) target = target.Single().Children; }
        var before = editor.Value;
        await (Task)Invoke(editor, "AddGraphNode", target)!;
        Assert.Equal(before, editor.Value);
        Assert.Contains("No step was added", (string)Field(editor, "_error")!);
    }

    [Fact]
    public async Task MalformedDefinitionNeverCreatesAGraphOrLosesOriginalJson()
    {
        var activator = new TrackingActivator();
        await using var services = Services(activator);
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            const string original = "{\"version\":1,\"nodes\":[null]}";
            var rendered = await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = original, ["ShowGraph"] = true }));
            Assert.Empty(activator.Created.OfType<NativeFlowGraph>());
            Assert.Contains("Original JSON is preserved", rendered.ToHtmlString());
            Assert.Equal(original, Assert.Single(activator.Created.OfType<NativeFlowEditor>()).Value);
        });
    }

    [Fact]
    public async Task DesignerOpensFromCanvasKeepsPerAugmentStateAcrossTabsAndSavesTheEditedNativeJson()
    {
        var activator = new TrackingActivator();
        var handler = new SavingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        await using var services = Services(activator, new MulseApiClient(http));
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Designer>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["FlowId"] = "native-visual" }));
            var designer = Assert.Single(activator.Created.OfType<Designer>());
            Assert.Contains("Edit steps in Augment 1", rendered.ToHtmlString());
            Assert.Empty(activator.Created.OfType<NativeFlowEditor>());
            var nodes = ((IEnumerable)Field(designer, "_nodes")!).Cast<object>().Where(node => (string)Property(node, "ModuleId")! == "native-control-flow-augment").ToArray();
            Invoke(designer, "OpenAugmentDesigner", nodes[0]);
            await designer.SetParametersAsync(ParameterView.Empty);
            var editors = activator.Created.OfType<NativeFlowEditor>().ToArray();
            Assert.Equal(2, editors.Length);
            Assert.All(editors, editor => Assert.True(editor.ShowGraph));
            Assert.Contains("id=\"flow-workspace\" class=\"flow-workspace\" role=\"tabpanel\" aria-labelledby=\"flow-workspace-tab\" hidden", rendered.ToHtmlString());
            var first = editors[0];
            var graph = activator.Created.OfType<NativeFlowGraph>().First();
            await graph.Selected.InvokeAsync("second");
            var nodeProperties = activator.Created.OfType<NativeFlowNodesEditor>().Last(editor => editor.FocusedNodeId == "second");
            var secondWait = nodeProperties.Nodes.Single(node => node.Id == "second");
            secondWait.DurationSeconds = 27;
            await nodeProperties.Changed.InvokeAsync();
            var edited = first.Value;
            Invoke(designer, "SetMappingWorkspace", true);
            await designer.SetParametersAsync(ParameterView.Empty);
            Assert.False((bool)Field(designer, "_showAugmentDesigner")!);
            Invoke(designer, "OpenAugmentDesigner", nodes[1]);
            await designer.SetParametersAsync(ParameterView.Empty);
            Invoke(designer, "OpenAugmentDesigner", nodes[0]);
            await designer.SetParametersAsync(ParameterView.Empty);
            Assert.Same(first, activator.Created.OfType<NativeFlowEditor>().First());
            Assert.Equal(2, activator.Created.OfType<NativeFlowEditor>().Count());
            Assert.Equal("second", Field(first, "_selectedNodeId"));
            Assert.Equal(edited, first.Value);
            await (Task)Invoke(designer, "SaveAsync")!;
            Assert.Null(Field(designer, "_errorMessage"));
            Assert.Equal(edited, handler.Saved!.Augments[0].Settings["flowJson"]);
            Assert.Equal(27, NativeFlowDefinition.Read(handler.Saved.Augments[0].Settings["flowJson"], allowBlocked: true).Nodes[0].Children[1].DurationSeconds);
            Assert.Throws<NotSupportedException>(() => NativeFlowDefinition.Read(handler.Saved.Augments[0].Settings["flowJson"]));
            Assert.Equal(handler.Original.Augments[1].Settings["flowJson"], handler.Saved.Augments[1].Settings["flowJson"]);
            Invoke(designer, "BuildFromFlow", handler.Original with { Augments = handler.Saved.Augments.Select(step => new FlowStepViewModel(step.Module, step.Settings)).ToArray() });
            var reopened = ((IEnumerable)Field(designer, "_nodes")!).Cast<object>().First(node => (string)Property(node, "ModuleId")! == "native-control-flow-augment");
            Assert.Equal(edited, ((Dictionary<string, string>)Property(reopened, "Settings")!)["flowJson"]);
        });
    }

    [Fact]
    public async Task AddingControlFlowIsExplicitAndKeepsTheOriginalAugmentChainOrder()
    {
        var designer = new Designer();
        Invoke(designer, "AddNativeAugment");
        Assert.Contains("Load the native", (string)Field(designer, "_errorMessage")!);
        Assert.Empty(((IEnumerable)Field(designer, "_nodes")!).Cast<object>());
        SetField(designer, "_modules", Modules);
        Invoke(designer, "AddNativeAugment");
        Invoke(designer, "AddNativeAugmentAt", -50d, 70d);
        var nodes = ((IEnumerable)Field(designer, "_nodes")!).Cast<object>().ToArray();
        Assert.Equal([0, 1], nodes.Select(node => (int)Property(node, "Order")!));
        Assert.Equal(-50d, Property(nodes[1], "X")); Assert.Equal(70d, Property(nodes[1], "Y"));
        Assert.Equal(Property(nodes[1], "Id"), Property(designer, "ActiveNativeAugmentId"));
        Assert.All(nodes, node => Assert.Empty(NativeFlowDefinition.Read(((Dictionary<string, string>)Property(node, "Settings")!)["flowJson"]).Nodes));
        Assert.True((bool)Field(designer, "_showAugmentDesigner")!);
        await Task.CompletedTask;
    }

    private static ParameterView Parameters(NativeFlowEditorModel model) => ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = JsonSerializer.Serialize(model, JsonSerializerOptions.Web), ["Modules"] = Modules, ["ShowGraph"] = true });
    private static ServiceProvider Services(TrackingActivator activator, IMulseApiClient? api = null)
    {
        var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator).AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).AddSingleton<NavigationManager>(new TestNavigation());
        if (api is not null) services.AddSingleton(api);
        return services.BuildServiceProvider();
    }
    private static int Count(string value, string needle) => value.Split(needle, StringSplitOptions.None).Length - 1;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Property(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static void SetField(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private sealed class TrackingActivator : IComponentActivator
    {
        public List<IComponent> Created { get; } = [];
        public IComponent CreateInstance(Type type) { var component = (IComponent)Activator.CreateInstance(type)!; Created.Add(component); return component; }
    }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/designer");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
    private sealed class SavingHandler : HttpMessageHandler
    {
        public UpdateFlowRequestViewModel? Saved { get; private set; }
        public FlowViewModel Original { get; } = new("native-visual", false, "OnDemand", null, false, ["fetch"], ["parse"], ["file-fetch"], ["json-parse"], ["native-control-flow-augment"], [], [],
            [new("fetch", new("file-fetch", new Dictionary<string, string>()), [])], [new("parse", new("json-parse", new Dictionary<string, string>()), ["fetch"])],
            [new("native-control-flow-augment", new Dictionary<string, string> { ["flowJson"] = JsonSerializer.Serialize(Definition(), JsonSerializerOptions.Web) }), new("native-control-flow-augment", new Dictionary<string, string> { ["flowJson"] = JsonSerializer.Serialize(new NativeFlowEditorModel { Durable = true, Nodes = [new() { Id = "second-owner", Kind = "Wait", DurationSeconds = 3 }] }, JsonSerializerOptions.Web) })], []);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            object result;
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/api/modules") result = Modules;
            else if (path == "/api/config-values/references") result = Array.Empty<string>();
            else if (path == "/api/config-values/usages") result = Array.Empty<ConfigReferenceUsageViewModel>();
            else if (path.EndsWith("/checkpoints", StringComparison.Ordinal)) result = Array.Empty<FlowCheckpointViewModel>();
            else if (path == "/api/flows/native-visual")
            {
                if (request.Method == HttpMethod.Put) Saved = await request.Content!.ReadFromJsonAsync<UpdateFlowRequestViewModel>(token);
                result = Original;
            }
            else throw new InvalidOperationException("Unexpected API request: " + path);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType()) };
        }
    }
}
