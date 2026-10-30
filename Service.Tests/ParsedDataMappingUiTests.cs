using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Mapping;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Models;

namespace Service.Tests;

public sealed class ParsedDataMappingUiTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    [Fact]
    public async Task ManualAndDiscoveredFields_ConnectPreviewAndReopenWithoutSavingSamples()
    {
        using var http = new HttpClient(new MappingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var settings = new Dictionary<string, string> { ["mappingJson"] = "{}" };
        var editor = Editor(settings, new MulseApiClient(http));
        Field(editor, "_newInput").SetValue(editor, "Order.Id"); Invoke(editor, "AddInput");
        Field(editor, "_newOutput").SetValue(editor, "InvoiceNumber"); Invoke(editor, "AddOutput");
        Field(editor, "_sample").SetValue(editor, "[{\"Order.Id\":\"sensitive-sample-only\",\"Amount\":12.5}]");
        await (Task)Invoke(editor, "InspectAsync")!;
        var map = (ParsedDataMapViewModel)Field(editor, "_map").GetValue(editor)!;
        Assert.Contains("/Amount", map.InputFields);
        Assert.Contains("/Order.Id", map.InputFields);
        Invoke(editor, "SelectInput", "/Order.Id"); Field(editor, "_selectedOutput").SetValue(editor, "/InvoiceNumber");
        Invoke(editor, "Connect");
        await (Task)Invoke(editor, "PreviewAsync")!;
        Assert.Contains("sensitive-sample-only", (string)Field(editor, "_preview").GetValue(editor)!);
        Assert.DoesNotContain("sensitive-sample-only", settings["mappingJson"]);
        Assert.Equal(ParsedDataMapping.Map(settings["mappingJson"], "[{\"Order.Id\":\"sensitive-sample-only\",\"Amount\":12.5}]"), Field(editor, "_preview").GetValue(editor));
        var reopened = Editor(new(settings), new MulseApiClient(http));
        var restored = (ParsedDataMapViewModel)Field(reopened, "_map").GetValue(reopened)!;
        Assert.Equal("/Order.Id", Assert.Single(restored.Fields).SourcePath);
        Assert.Null(Field(reopened, "_parsed").GetValue(reopened));
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http))
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ParsedDataMappingEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Settings"] = settings }));
            var html = rendered.ToHtmlString();
            Assert.Contains("aria-label=\"Input fields\"", html);
            Assert.Contains("aria-label=\"Output fields\"", html);
            Assert.Contains("Connect fields", html);
            Assert.Contains("Discover input fields", html);
            Assert.Contains("Raw sample through source parser", html);
        });
    }

    [Fact]
    public void InvalidOrFutureJson_IsNotSilentlyReplacedByGraphicalEditing()
    {
        var settings = new Dictionary<string, string> { ["mappingJson"] = "{\"version\":2,\"future\":true}" };
        using var http = new HttpClient(new MappingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var editor = Editor(settings, new MulseApiClient(http));
        Assert.True((bool)Field(editor, "_loadFailed").GetValue(editor)!);
        Assert.Contains("Unable to load", (string)Field(editor, "_error").GetValue(editor)!);
        Assert.Contains("future", settings["mappingJson"]);
        Invoke(editor, "ReplaceJson", new ChangeEventArgs { Value = "{}" });
        Assert.False((bool)Field(editor, "_loadFailed").GetValue(editor)!);
    }

    [Fact]
    public async Task Designer_AddSaveRenameReopen_PreservesAugmentMappingAndRejectsRemovedSources()
    {
        var handler = new MappingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var designer = new Designer();
        Property(designer, "ApiClient").SetValue(designer, new MulseApiClient(http));
        Property(designer, "Navigation").SetValue(designer, new TestNavigation());
        Field(designer, "_modules").SetValue(designer, new ModuleViewModel[]
        {
            new("parsed-data-map-augment", "Map parsed data", "OrchestrationAugment", "Maps fields", [], [], [], []),
            new("file-system-fetch", "Files", "Fetch", "Fetch files", [], [], [], []),
            new("json-parse", "JSON", "Parse", "Parse JSON", [], [], [], [])
        });
        var pipeline = new PipelineDefinition
        {
            Id = "sheet-map", Fetches = [new() { Id = "fetch", Fetch = new() { Module = "file-system-fetch" } }],
            Parses = [new() { Id = "sheet", FetchNodeIds = ["fetch"], Parse = new() { Module = "excel-parse" } }],
            Deliveries = [new() { Render = new() { Module = "json-render" }, Delivers = [new() { Deliver = new() { Module = "logging-deliver" } }] }]
        };
        Invoke(designer, "BuildFromFlow", View(pipeline));
        Invoke(designer, "AddParsedDataMapping");
        Assert.True((bool)Field(designer, "_showMappingDesigner").GetValue(designer)!);
        var node = Nodes(designer).Single(node => (string)Property(node, "ModuleId").GetValue(node)! == "parsed-data-map-augment");
        var settings = (Dictionary<string, string>)Property(node, "Settings").GetValue(node)!;
        settings["mappingJson"] = ParsedDataMappingTests.MapJson(new ParsedDataFieldMap { SourcePath = "/OrderId", TargetPath = "/Id" });
        Assert.Equal("sheet", settings["sourceId"]);
        var parse = Nodes(designer).Single(node => Property(node, "Stage").GetValue(node)!.ToString() == "Parse");
        Invoke(designer, "RenameGraphNode", parse, "orders");
        Assert.Equal("orders", settings["sourceId"]);
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field(designer, "_errorMessage").GetValue(designer));
        Assert.Equal(settings["mappingJson"], Assert.Single(handler.Saved!.Augments).Settings["mappingJson"]);
        Invoke(designer, "BuildFromFlow", View(handler.Saved));
        var reopened = Nodes(designer).Single(node => (string)Property(node, "ModuleId").GetValue(node)! == "parsed-data-map-augment");
        Assert.Equal(settings["mappingJson"], ((Dictionary<string, string>)Property(reopened, "Settings").GetValue(reopened)!)["mappingJson"]);
        var reopenedParse = Nodes(designer).Single(node => Property(node, "Stage").GetValue(node)!.ToString() == "Parse");
        Invoke(designer, "RemoveParseNode", reopenedParse, true);
        Invoke(designer, "AddSourceLane");
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Contains("missing source 'orders'", (string)Field(designer, "_errorMessage").GetValue(designer)!);
    }

    private static ParsedDataMappingEditor Editor(Dictionary<string, string> settings, IMulseApiClient client)
    {
        var editor = new ParsedDataMappingEditor();
        Property(editor, "Settings").SetValue(editor, settings); Property(editor, "ApiClient").SetValue(editor, client);
        Invoke(editor, "OnParametersSet"); return editor;
    }
    private static FlowViewModel View(PipelineDefinition pipeline) => JsonSerializer.Deserialize<FlowViewModel>(JsonSerializer.Serialize(FlowMappings.MapFlow(pipeline), Options), Options)!;
    private static IEnumerable<object> Nodes(Designer designer) => ((IEnumerable)Field(designer, "_nodes").GetValue(designer)!).Cast<object>();
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static PropertyInfo Property(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/designer");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
    }
    private sealed class MappingHandler : HttpMessageHandler
    {
        public PipelineDefinition? Saved;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object result;
            if (request.RequestUri!.AbsolutePath.EndsWith("/inspect", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadFromJsonAsync<ParsedDataSampleRequestViewModel>(ct);
                result = ParsedDataMapping.Inspect(body!.Text, body.Format, body.RecordPath);
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadFromJsonAsync<ParsedDataPreviewRequestViewModel>(ct);
                result = new ParsedDataPreviewViewModel(ParsedDataMapping.Map(body!.MappingJson, body.ParsedData, body.Format));
            }
            else if (request.Method == HttpMethod.Post)
            {
                Saved = FlowMappings.MapPipeline((await request.Content!.ReadFromJsonAsync<CreateFlowRequest>(Options, ct))!);
                result = FlowMappings.MapFlow(Saved);
            }
            else if (request.RequestUri.AbsolutePath.Contains("config-values", StringComparison.Ordinal)) result = Array.Empty<string>();
            else throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result, result.GetType(), options: Options) };
        }
    }
}
