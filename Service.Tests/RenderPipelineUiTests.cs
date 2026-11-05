using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Mapping;
using Mulse.Modules.Orchestration;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;

namespace Service.Tests;

public sealed class RenderPipelineUiTests
{
    private static readonly ModuleViewModel[] Modules =
    [
        new("first", "First renderer", "Render", "First stage", [new("option", "Stage option", "Native option", false, "Text", "default", [])], [], [], []),
        new("second", "Second renderer", "Render", "Second stage", [], [], [], []),
        new("delivery", "Delivery", "Deliver", "Destination", [new("path", "Destination path", "Native path", true, "Text", "default-path", [])], [], [], []),
        new("render-pipeline", "Pipeline", "Render", "Pipeline", [new("stagesJson", "Stages", "Stages", true, "TextArea", "[]", [])], [], [], [])
    ];
    private static object? Invoke(object instance, string name, params object?[] args) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args);
    private static void Set(object instance, string name, object? value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static object[] Items(object instance, string name) => ((IEnumerable)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!).Cast<object>().ToArray();
    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
    private static string Source()
    {
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string type, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", type), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Scratch", N("ServiceBody", "Body",
            N("Send", "Native outbound", P("PortName", "out"), P("MessageName", "request")),
            N("Send", "Native reply", P("PortName", "input"), P("MessageName", "response"), P("OperationMessageName", "Response")))));
        return JsonSerializer.Serialize(OdxProgramReader.Read("<?xml version='1.0'?>" + source), JsonSerializerOptions.Web);
    }

    [Fact]
    public async Task VisualStagesAddSelectReorderSaveReopenAndExecuteInAScratchFlow()
    {
        var editor = new RenderPipelineEditor(); string saved = "[]";
        Set(editor, "Value", saved); Set(editor, "Modules", Modules);
        Set(editor, "ValueChanged", EventCallback.Factory.Create<string>(new object(), value => saved = value));
        Invoke(editor, "OnParametersSet");
        await (Task)Invoke(editor, "Add")!;
        var first = Items(editor, "_stages")[0];
        await (Task)Invoke(editor, "ChangeModule", first, "first")!;
        await (Task)Invoke(editor, "Add")!;
        var second = Items(editor, "_stages")[1];
        await (Task)Invoke(editor, "ChangeModule", second, "second")!;
        await (Task)Invoke(editor, "Move", second, -1)!;
        var parsed = RenderPipelineDefinition.Read(saved);
        Assert.Equal(new[] { "second", "first" }, parsed.Select(stage => stage.Module)); Assert.Equal("default", parsed[1].Settings["option"]);
        var html = await Render<RenderPipelineEditor>(new() { ["Value"] = saved, ["Modules"] = Modules });
        Assert.Contains("Move stage earlier", html); Assert.Contains("Add renderer stage", html); Assert.Contains("Stage option", html);
        Assert.DoesNotContain("render-pipeline (render-pipeline)", html);

        var visited = new List<string>(); var delivered = new List<IntegrationBatch>();
        var input = new IntegrationBatch([new("input", BinaryData.FromString("value"), "text/plain", new Dictionary<string, string>())]);
        var catalog = new FakeExecutableModuleCatalog().AddFetch(DelegateModuleFactory.Fetch("fetch", input)).AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(new DelegateRenderModule("second", (_, batch, _, _) =>
            {
                visited.Add("second"); return Task.FromResult(new IntegrationBatch([batch.Payloads[0] with { Content = BinaryData.FromString("second-output") }]));
            }))
            .AddRender(new DelegateRenderModule("first", (_, batch, step, _) =>
            {
                Assert.Equal("second-output", batch.Payloads.Single().GetText()); Assert.Equal("default", step.Settings["option"]); visited.Add("first"); return Task.FromResult(batch);
            })).AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
        await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        catalog.AddRender(new RenderPipelineModule(new Service.Modules.RenderModuleInvoker(provider)));
        var flow = new PipelineDefinition { Id = "scratch", Enabled = true,
            Fetches = [new() { Id = "f", Fetch = new() { Module = "fetch" } }], Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
            Deliveries = [new() { Render = new() { Module = "render-pipeline", Settings = new() { ["stagesJson"] = saved } }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }] };
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, new InMemoryFlowOrchestrationStateStore(), new InMemoryFlowRetryStateStore(),
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
        Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ExecuteAsync("scratch", default)).Outcome);
        Assert.Equal(new[] { "second", "first" }, visited); Assert.Equal("second-output", delivered.Single().Payloads.Single().GetText());
        await (Task)Invoke(editor, "Remove", first)!; Assert.Single(RenderPipelineDefinition.Read(saved));
    }

    [Fact]
    public async Task VisualSendSelectionUsesSourceNamesExcludesRepliesAndPreservesStagesOnDeliveryChange()
    {
        var settings = new Dictionary<string, string> { ["programJson"] = Source(), ["sendsJson"] = "[]" };
        var editor = new OdxSendEditor(); Set(editor, "Settings", settings); Set(editor, "Modules", Modules); Invoke(editor, "OnParametersSet");
        Assert.Single(Items(editor, "_targets")); await (Task)Invoke(editor, "Add")!;
        var send = Items(editor, "_sends").Single(); await (Task)Invoke(editor, "ChangeModule", send, "delivery")!;
        await (Task)Invoke(editor, "ChangePipeline", send, "[{\"module\":\"first\",\"settings\":{\"option\":\"custom\"}}]")!;
        await (Task)Invoke(editor, "SetResponse", send, "true")!;
        var saved = OdxSendBindings.Read(settings["sendsJson"]).Single(); Assert.Equal("default-path", saved.Settings["path"]); Assert.Single(saved.Pipeline);
        Assert.Equal("true", saved.Settings["expectsResponse"]);
        var html = await Render<OdxSendEditor>(new() { ["Settings"] = settings, ["Modules"] = Modules });
        Assert.Contains("Native outbound", html); Assert.DoesNotContain("Native reply", html); Assert.Contains("Destination path", html); Assert.Contains("Add renderer stage", html);
        Assert.Contains("Capture transport response", html);
        await (Task)Invoke(editor, "ChangeModule", send, "DELIVERY")!;
        Assert.Equal("custom", OdxSendBindings.Read(settings["sendsJson"]).Single().Pipeline[0].Settings["option"]);
        var before = settings["sendsJson"];
        await (Task)Invoke(editor, "ChangePipeline", send, "[null]")!;
        Invoke(editor, "OnParametersSet"); Assert.Equal(before, settings["sendsJson"]);
        Assert.Equal("[null]", send.GetType().GetProperty("PipelineDraft")!.GetValue(send));
        await (Task)Invoke(editor, "ChangePipeline", send, "[]")!; Assert.Empty(OdxSendBindings.Read(settings["sendsJson"]).Single().Pipeline);
        await (Task)Invoke(editor, "Remove", send)!; Assert.Empty(OdxSendBindings.Read(settings["sendsJson"]));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("not-json")]
    [InlineData("[{\"module\":\"first\",\"settings\":{},\"ignored\":true}]")]
    public async Task InvalidStagesRemainVisibleAndAreNotReplaced(string value)
    {
        var html = await Render<RenderPipelineEditor>(new() { ["Value"] = value, ["Modules"] = Modules });
        Assert.Contains("Original JSON is preserved", html); Assert.DoesNotContain("Add renderer stage", html);
        Assert.Throws<ArgumentException>(() => RenderPipelineDefinition.Read(value));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void PipelineLimitsMatchRuntime(int count, bool reject)
    {
        var json = JsonSerializer.Serialize(Enumerable.Range(0, count).Select(_ => new RenderPipelineStage("first", new Dictionary<string, string>())), JsonSerializerOptions.Web);
        if (reject) Assert.Throws<ArgumentException>(() => RenderPipelineDefinition.Read(json)); else Assert.Equal(count, RenderPipelineDefinition.Read(json).Count);
    }

    [Fact]
    public async Task PipelineFailureStopsLaterStagesAndDoesNotDeliver()
    {
        var invoker = new FailingInvoker(); var module = new RenderPipelineModule(invoker);
        var step = new ModuleStepDefinition { Module = "render-pipeline", Settings = new() { ["stagesJson"] = "[{\"module\":\"first\",\"settings\":{}},{\"module\":\"second\",\"settings\":{}}]" } };
        await Assert.ThrowsAsync<IOException>(() => module.RenderAsync(new("scratch", "run", DateTimeOffset.UtcNow), IntegrationBatch.Empty, step, default));
        Assert.Equal(new[] { "first" }, invoker.Calls);
        step.Settings["stagesJson"] = "[{\"module\":\"render-pipeline\",\"settings\":{}}]";
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(new("scratch", "run", DateTimeOffset.UtcNow), IntegrationBatch.Empty, step, default));
        Assert.Single(invoker.Calls);
    }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException("Static rendering does not execute JavaScript.");
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException("Static rendering does not execute JavaScript.");
    }
    private sealed class FailingInvoker : IRenderModuleInvoker
    {
        public List<string> Calls { get; } = [];
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        { Calls.Add(id); throw new IOException("Synthetic stage failure"); }
    }
}
