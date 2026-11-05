using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Mapping;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class RenderDispatchTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly FlowExecutionContext Context = new("dispatch", "test", DateTimeOffset.UtcNow);

    [Fact]
    public async Task DynamicChoices_InvokeRealMaps_InInputOrder_AndValidateAllSelectionsFirst()
    {
        var calls = new List<string>();
        var catalog = new FakeExecutableModuleCatalog().AddRender(new XmlMappingRenderModule())
            .AddRender(new DelegateRenderModule("observed", (_, batch, _, _) => { calls.Add(batch.Payloads.Single().Name); return Task.FromResult(batch); }));
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
            .AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>().BuildServiceProvider();
        var module = new RenderDispatchModule(services.GetRequiredService<IRenderModuleInvoker>());
        var step = new ModuleStepDefinition { Module = "render-dispatch", Settings = new()
        {
            ["metadataKey"] = "MapName", ["casesJson"] = JsonSerializer.Serialize(new RenderDispatchCase[]
            {
                new() { Value = "map", Module = "xml-mapping-render", Settings = new() { ["mappingJson"] = XmlMappingTests.Json() } },
                new() { Value = "pass", Module = "observed" }
            }, Options)
        } };
        var first = Payload("first", "map"); var second = Payload("second", "pass"); var third = Payload("third", "map");
        var result = await module.RenderAsync(Context, new([first, second, third]), step, default);
        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { "first.xml", "second", "third.xml" }, result.Payloads.Select(payload => payload.Name));
        Assert.Equal("42", XDocument.Parse(result.Payloads[0].GetText()).Descendants(XName.Get("Number", "urn:target")).Single().Value);
        Assert.Equal(new[] { "second" }, calls);
        calls.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context, new([second, Payload("unknown", "MAP")]), step, default));
        Assert.Empty(calls);
        Assert.Empty((await module.RenderAsync(Context, IntegrationBatch.Empty, step, default)).Payloads);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.RenderAsync(Context, new([first]), step, new CancellationToken(true)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitGrouping_IsStableAndBySelection_NotRenderer_AndPrevalidatesAllInputs(bool midFlow)
    {
        var calls = new List<string>();
        var catalog = new FakeExecutableModuleCatalog().AddRender(new DelegateRenderModule("combine", (_, batch, step, _) =>
        {
            var label = step.Settings["label"];
            calls.Add(label + ":" + string.Join(",", batch.Payloads.Select(payload => payload.Name)));
            return Task.FromResult(new IntegrationBatch([batch.Payloads[0] with { Name = label, Content = BinaryData.FromString(calls[^1]) }]));
        }));
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
            .AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>().BuildServiceProvider();
        var invoker = services.GetRequiredService<IRenderModuleInvoker>();
        var step = new ModuleStepDefinition { Settings = new()
        {
            ["metadataKey"] = "MapName", ["batchMode"] = "PerSelection", ["casesJson"] = JsonSerializer.Serialize(new[]
            {
                new RenderDispatchCase { Value = "A", Module = "combine", Settings = new() { ["label"] = "Client A" } },
                new RenderDispatchCase { Value = "B", Module = "combine", Settings = new() { ["label"] = "Client B" } }
            }, Options)
        } };
        step = JsonSerializer.Deserialize<ModuleStepDefinition>(JsonSerializer.Serialize(step, Options), Options)!;
        Task<IntegrationBatch> Execute(IntegrationBatch batch) => midFlow
            ? new Mulse.Modules.BuiltIn.Augment.MapDispatchAugmentModule(invoker).AugmentAsync(Context, batch, step, default)
            : new RenderDispatchModule(invoker).RenderAsync(Context, batch, step, default);
        var input = new IntegrationBatch([Payload("b1", "B"), Payload("a1", "A"), Payload("b2", "B"), Payload("a2", "A")]);
        var output = await Execute(input);
        Assert.Equal(new[] { "Client B:b1,b2", "Client A:a1,a2" }, calls);
        Assert.Equal(new[] { "Client B", "Client A" }, output.Payloads.Select(payload => payload.Name));
        Assert.Equal(new[] { "b1", "a1", "b2", "a2" }, input.Payloads.Select(payload => payload.Name));
        calls.Clear();
        await Assert.ThrowsAsync<ArgumentException>(() => Execute(new([Payload("a1", "A"), Payload("unknown", "a")])));
        Assert.Empty(calls);
        Assert.Empty((await Execute(IntegrationBatch.Empty)).Payloads);
        step.Settings["batchMode"] = "Unsupported";
        await Assert.ThrowsAsync<ArgumentException>(() => Execute(input));
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"value\":\"A\",\"module\":\"render-dispatch\"}]")]
    [InlineData("[{\"value\":\"A\",\"module\":\"json-render\"},{\"value\":\"A\",\"module\":\"xml-render\"}]")]
    [InlineData("[{\"value\":\"A\",\"module\":\"json-render\",\"settings\":null}]")]
    [InlineData("[{\"value\":\"A\",\"module\":\"json-render\",\"future\":true}]")]
    public void InvalidOrRecursiveChoices_AreRejected(string json) => Assert.Throws<ArgumentException>(() => RenderDispatchDefinition.Read(json));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dependencies_IncludeEveryChoice_ResolveConfig_AndBlockUnknownRenderers(bool midFlow)
    {
        var step = new ModuleStepDefinition { Module = midFlow ? "map-dispatch-augment" : "render-dispatch", Settings = new() { ["casesJson"] = "{{config:choices}}" } };
        var flow = new PipelineDefinition { Id = "dispatch", Augments = midFlow ? [step] : [], Deliveries = midFlow ? [] : [new() { Render = step }] };
        var config = new Dictionary<string, string> { ["choices"] = "[{\"value\":\"A\",\"module\":\"plugin-map\"}]" };
        Assert.Contains(("plugin-map", ModuleKind.Render), FlowModuleDependencies.References(flow, config));
        var catalog = FakeModuleCatalog.WithModules(("render-dispatch", ModuleKind.Render), ("map-dispatch-augment", ModuleKind.OrchestrationAugment));
        Assert.Contains("Render: plugin-map", FlowModuleDependencies.FindMissing(flow, catalog.GetAll(), config));
        Assert.Contains(FlowModuleDependencies.FindMissing(flow, catalog.GetAll()), error => error.Contains("choices"));
        config["choices"] = "[{\"value\":\"A\",\"module\":\"render-dispatch\"}]";
        Assert.Contains(FlowModuleDependencies.FindMissing(flow, catalog.GetAll(), config), error => error.Contains("not render-dispatch"));
    }

    [Fact]
    public async Task VisualChoices_RoundTripDescriptorSettings_AndPreserveUnknownJsonForRepair()
    {
        var settings = new Dictionary<string, string>();
        var editor = new RenderDispatchEditor();
        typeof(RenderDispatchEditor).GetProperty("Settings")!.SetValue(editor, settings);
        Invoke(editor, "OnParametersSet"); Invoke(editor, "Add");
        var choice = Assert.Single(Assert.IsAssignableFrom<IEnumerable>(Field(editor, "_choices")).Cast<object>());
        choice.GetType().GetProperty("Value")!.SetValue(choice, "A");
        Invoke(editor, "ChangeModule", choice, new ChangeEventArgs { Value = "json-render" });
        Invoke(editor, "SetSetting", choice, "indented", "true");
        var captured = Assert.Single(RenderDispatchDefinition.Read(settings["casesJson"]));
        Assert.Equal("true", captured.Settings["indented"]);
        Invoke(editor, "OnParametersSet");
        Assert.Equal("true", captured.Settings["indented"]);
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http))
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<RenderDispatchEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Settings"] = settings, ["Modules"] = new ModuleViewModel[] { new("json-render", "JSON render", "Render", "Serializes JSON", [new("indented", "Indented", "Readable JSON", false, "Boolean", "false", [])], [], [], []) }
            }));
            Assert.Contains("Selected renderer module", rendered.ToHtmlString());
            Assert.Contains("Indented", rendered.ToHtmlString());
            Assert.Contains("Renderer input grouping", rendered.ToHtmlString());
            Assert.Contains("Group current batch by selection", rendered.ToHtmlString());
            Assert.Contains("BizTalk development tools and reference input/output files are not required", rendered.ToHtmlString());
            Assert.Contains("Reference fixtures are optional parity evidence", rendered.ToHtmlString());
        });
        const string unknown = "[{\"future\":true}]";
        Invoke(editor, "ReplaceJson", new ChangeEventArgs { Value = unknown });
        Assert.Equal(true, Field(editor, "_loadFailed"));
        Assert.Equal(unknown, settings["casesJson"]);
    }

    private static IntegrationPayload Payload(string name, string selection) => new(name, BinaryData.FromString(XmlMappingTests.Sample), "application/xml", new Dictionary<string, string> { ["MapName"] = selection });
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
}
