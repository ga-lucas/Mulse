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
        Invoke(editor, "AddInputMapping", "/Order.Id"); Invoke(editor, "AssignOutput", "/InvoiceNumber");
        Invoke(editor, "Connect");
        Assert.Single(map.Fields);
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
    public void ClickAndDragMappings_AssignOutputsWithoutDuplicatesOrOverlaps()
    {
        using var http = new HttpClient(new MappingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var settings = new Dictionary<string, string> { ["mappingJson"] = "{}" };
        var editor = Editor(settings, new MulseApiClient(http));
        Invoke(editor, "AddInputMapping", "/id");
        var map = (ParsedDataMapViewModel)Field(editor, "_map").GetValue(editor)!;
        var first = Assert.Single(map.Fields);
        Assert.Equal("/id", first.SourcePath);
        Invoke(editor, "AssignOutput", "/invoice/id");
        Assert.Equal("/invoice/id", first.TargetPath);
        Invoke(editor, "BeginInputDrag", "/amount"); Invoke(editor, "DropOnConnections");
        var second = map.Fields[1];
        Assert.Equal("/amount", second.SourcePath);
        Invoke(editor, "BeginMappingDrag", second); Invoke(editor, "DropOnOutput", "/total");
        Assert.Equal("/total", second.TargetPath);
        Invoke(editor, "BeginOutputDrag", "/renamed"); Invoke(editor, "DropOnMapping", first);
        Assert.Equal("/renamed", first.TargetPath);
        Invoke(editor, "AssignOutput", "/total/nested");
        Assert.Equal("/renamed", first.TargetPath);
        Assert.Contains("overlaps", (string)Field(editor, "_error").GetValue(editor)!);
        Invoke(editor, "BeginInputDrag", "/third"); Invoke(editor, "DropOnOutput", "/thirdOutput");
        Assert.Equal("/third", map.Fields[2].SourcePath);
        Invoke(editor, "AddInputMapping", "/thirdOutput/child");
        Assert.Equal("/output1", map.Fields[3].TargetPath);
        Invoke(editor, "AddInputMapping", "");
        Assert.Equal("", map.Fields[4].SourcePath);
        var count = map.Fields.Count;
        Invoke(editor, "BeginInputDrag", "/ignored"); Invoke(editor, "ClearDrag"); Invoke(editor, "DropOnConnections");
        Assert.Equal(count, map.Fields.Count);
        Invoke(editor, "BeginInputDrag", "/blocked"); Field(editor, "_busy").SetValue(editor, true); Invoke(editor, "DropOnConnections");
        Assert.Equal(count, map.Fields.Count);
        Field(editor, "_busy").SetValue(editor, false);
        Invoke(editor, "Remove", map.Fields[^1]);
        Assert.Null(Field(editor, "_selectedMapping").GetValue(editor));
        Invoke(editor, "AssignOutput", "/none");
        Assert.Contains("Select a mapping", (string)Field(editor, "_error").GetValue(editor)!);
        first.TargetPath = "";
        Invoke(editor, "AddInputMapping", "/new");
        Assert.Equal("/new", map.Fields[^1].TargetPath);
        first.TargetPath = "/renamed"; Invoke(editor, "Persist");
        map.Fields[^1].SourcePath = "/changedInput"; map.Fields[^1].TargetPath = "/changedOutput";
        Invoke(editor, "Persist"); Invoke(editor, "Connect");
        Assert.Equal("/changedOutput", map.Fields[^1].TargetPath);
        Assert.Equal("/changedInput", Field(editor, "_selectedInput").GetValue(editor));
        Assert.Equal(map.Fields.Count, ParsedDataMapping.ReadMap(settings["mappingJson"]).Fields.Count);
    }

    [Fact]
    public async Task TransformEditor_PreservesArgumentsSettingsAndPreviewAfterReopen()
    {
        using var http = new HttpClient(new MappingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var settings = new Dictionary<string, string> { ["mappingJson"] = "{}" };
        var client = new MulseApiClient(http);
        var editor = Editor(settings, client);
        Invoke(editor, "AddInputMapping", "/first");
        var field = Assert.Single(((ParsedDataMapViewModel)Field(editor, "_map").GetValue(editor)!).Fields);
        field.Kind = "Transform"; Invoke(editor, "KindChanged", field);
        Assert.Equal("/first", Assert.Single(field.TransformInputs).SourcePath);
        field.TransformModule = "concat-transform";
        Invoke(editor, "BeginInputDrag", "/last"); Invoke(editor, "DropOnMapping", field);
        Assert.Equal("/last", field.TransformInputs[1].SourcePath);
        var transformEditor = new ParsedMappingTransformEditor();
        Property(transformEditor, "Inputs").SetValue(transformEditor, field.TransformInputs);
        Property(transformEditor, "Settings").SetValue(transformEditor, field.TransformSettings);
        await (Task)Invoke(transformEditor, "MoveInputAsync", field.TransformInputs[1], -1)!;
        await (Task)Invoke(transformEditor, "AddInputAsync")!;
        field.TransformInputs[2].Kind = "Constant"; field.TransformInputs[2].ConstantJson = "\"fixed\"";
        await (Task)Invoke(transformEditor, "SetSettingAsync", "separator", "-")!;
        Invoke(editor, "AssignOutput", "/combined");
        Field(editor, "_sample").SetValue(editor, "{\"first\":\"A\",\"last\":\"B\"}");
        await (Task)Invoke(editor, "InspectAsync")!;
        await (Task)Invoke(editor, "PreviewAsync")!;
        Assert.Null(Field(editor, "_error").GetValue(editor));
        Assert.Equal("[{\"combined\":\"B-A-fixed\"}]", Field(editor, "_preview").GetValue(editor));
        var reopened = Editor(new(settings), client);
        var restored = Assert.Single(((ParsedDataMapViewModel)Field(reopened, "_map").GetValue(reopened)!).Fields);
        Assert.Equal("Transform", restored.Kind);
        Assert.Equal("concat-transform", restored.TransformModule);
        Assert.Equal("-", restored.TransformSettings["separator"]);
        Assert.Equal(new[] { "/last", "/first", "" }, restored.TransformInputs.Select(input => input.Kind == "Constant" ? "" : input.SourcePath));
        Assert.Equal("\"fixed\"", restored.TransformInputs[2].ConstantJson);
        Assert.Null(Field(reopened, "_parsed").GetValue(reopened));
        var module = new ModuleViewModel("concat-transform", "Concatenate", "Transform", "Combines input values", [new("separator", "Separator", "Between values", false, "Text", "", [])], [], [], []);
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(client)
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ParsedDataMappingEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Settings"] = settings, ["TransformModules"] = new[] { module } }));
            var html = rendered.ToHtmlString();
            Assert.Contains("Concatenate (concat-transform)", html);
            Assert.Contains("Transform inputs (in order)", html);
            Assert.Contains("Separator", html);
            Assert.Contains("draggable=\"true\"", html);
            Assert.Contains("Select mapping:", html);
        });
    }

    [Fact]
    public async Task NestedTemplatesConditionsAndChains_AreUiEditableAndSurvivePreviewAndReopen()
    {
        using var http = new HttpClient(new MappingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var settings = new Dictionary<string, string> { ["mappingJson"] = "{}" };
        var client = new MulseApiClient(http);
        var editor = Editor(settings, client);
        Field(editor, "_sample").SetValue(editor, """{"enabled":true,"name":"sample-private","orders":[{"id":"A","active":true,"lines":[{"sku":"x"}]},{"id":"B","active":false,"lines":[]}]}""");
        await (Task)Invoke(editor, "InspectAsync")!;
        Invoke(editor, "AddInputMapping", "/orders");
        var map = (ParsedDataMapViewModel)Field(editor, "_map").GetValue(editor)!;
        var orders = Assert.Single(map.Fields); orders.Kind = "Array"; Invoke(editor, "KindChanged", orders);
        Invoke(editor, "AssignOutput", "/invoices"); Invoke(editor, "OpenChildren", orders);
        Assert.Null(Field(editor, "_selectedMapping").GetValue(editor));
        Assert.Contains("/id", orders.InputFields); Assert.Contains("/lines/0/sku", orders.InputFields);
        Invoke(editor, "AddInputMapping", "/id");
        var id = Assert.Single(orders.Fields); Invoke(editor, "AssignOutput", "/number"); Invoke(editor, "AddCondition", id);
        id.Conditions[0].SourcePath = "/active"; id.Conditions[0].Operator = "Equals"; id.Conditions[0].ValueJson = "true";
        id.Otherwise = "Constant"; id.OtherwiseJson = "\"draft\"";
        Invoke(editor, "AddInputMapping", "/lines");
        var lines = orders.Fields[1]; lines.Kind = "Array"; Invoke(editor, "KindChanged", lines); Invoke(editor, "AssignOutput", "/items");
        Invoke(editor, "OpenChildren", lines); Assert.Contains("/sku", lines.InputFields);
        Invoke(editor, "AddInputMapping", "/sku"); var sku = Assert.Single(lines.Fields);
        Invoke(editor, "AddChainStep", sku); sku.TransformChain[0].Module = "uppercase-transform";
        Invoke(editor, "AddChainStep", sku); sku.TransformChain[1].Module = "concat-transform";
        sku.TransformChain[1].Settings["separator"] = "-";
        sku.TransformChain[1].Inputs.Add(new() { Kind = "Constant", ConstantJson = "\"mapped\"" });
        Invoke(editor, "MoveChainStep", sku, sku.TransformChain[1], -1);
        Invoke(editor, "BeginInputDrag", "/extra"); Invoke(editor, "DropOnTransform", sku, sku.TransformChain[0].Inputs);
        Assert.Equal("/extra", sku.TransformChain[0].Inputs[^1].SourcePath); Assert.Equal("/sku", sku.SourcePath);
        sku.TransformChain[0].Inputs.RemoveAt(sku.TransformChain[0].Inputs.Count - 1);
        Invoke(editor, "AddCondition", sku); sku.Conditions[0].SourcePath = "/enabled"; sku.Conditions[0].SourceScope = "Root";
        sku.Conditions[0].Operator = "Equals"; sku.Conditions[0].ValueJson = "true";
        Invoke(editor, "AddInputMapping", "/id"); lines.Fields[1].SourceScope = "Parent"; Invoke(editor, "AssignOutput", "/invoice");
        Field(editor, "_newInput").SetValue(editor, "manual"); Invoke(editor, "AddInput");
        Assert.Contains("/manual", lines.InputFields); Assert.DoesNotContain("/manual", map.InputFields);
        Invoke(editor, "LeaveScope"); Invoke(editor, "LeaveScope");
        Invoke(editor, "AddInputMapping", "/name"); var title = map.Fields[1]; Invoke(editor, "AssignOutput", "/title");
        Invoke(editor, "AddChainStep", title); title.TransformChain[0].Module = "uppercase-transform"; Invoke(editor, "Persist");
        await (Task)Invoke(editor, "PreviewAsync")!;
        Assert.Null(Field(editor, "_error").GetValue(editor));
        Assert.Equal("""[{"invoices":[{"number":"A","items":[{"sku":"X-MAPPED","invoice":"A"}]},{"number":"draft","items":[]}],"title":"SAMPLE-PRIVATE"}]""", Field(editor, "_preview").GetValue(editor));
        Assert.DoesNotContain("sample-private", settings["mappingJson"]);
        var saved = settings["mappingJson"];
        var reopened = Editor(new(settings), client);
        Assert.Null(Field(reopened, "_parsed").GetValue(reopened));
        var restored = (ParsedDataMapViewModel)Field(reopened, "_map").GetValue(reopened)!;
        Invoke(reopened, "OpenChildren", restored.Fields[0]); Invoke(reopened, "OpenChildren", restored.Fields[0].Fields[1]);
        Assert.Contains("/manual", restored.Fields[0].Fields[1].InputFields);
        Assert.Equal("concat-transform", restored.Fields[0].Fields[1].Fields[0].TransformChain[0].Module);
        Assert.Equal(saved, settings["mappingJson"]);
        Assert.Equal("Parent", ParsedDataMapping.ReadMap(saved).Fields[0].Fields[1].Fields[1].SourceScope);
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(client)
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ParsedDataMappingEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Settings"] = settings }));
            var html = rendered.ToHtmlString();
            Assert.Contains("Mapping scope", html); Assert.Contains("Repeating array", html); Assert.Contains("Object template", html);
            Assert.Contains("Edit child mappings (2)", html); Assert.Contains("Add condition", html); Assert.Contains("Otherwise", html);
            Assert.Contains("Add chain step", html); Assert.Contains("Previous result", html); Assert.Contains("Move step up", html);
        });
    }

    [Fact]
    public async Task TypedConstants_AcceptTextNumbersBooleansAndNullWithoutJsonAuthoring()
    {
        var valueEditor = new ParsedMappingValueInput();
        string? saved = null;
        Property(valueEditor, "ValueChanged").SetValue(valueEditor, EventCallback.Factory.Create<string>(new object(), (string value) => saved = value));
        Invoke(valueEditor, "OnParametersSet");
        await (Task)Invoke(valueEditor, "TypeChangedAsync", new ChangeEventArgs { Value = "Text" })!;
        await (Task)Invoke(valueEditor, "ValueChangedAsync", new ChangeEventArgs { Value = "quoted \"text\"" })!;
        Assert.Equal("quoted \"text\"", JsonSerializer.Deserialize<string>(saved!));
        await (Task)Invoke(valueEditor, "TypeChangedAsync", new ChangeEventArgs { Value = "Number" })!;
        await (Task)Invoke(valueEditor, "ValueChangedAsync", new ChangeEventArgs { Value = "12.5" })!;
        Assert.Equal(12.5m, JsonSerializer.Deserialize<decimal>(saved!));
        await (Task)Invoke(valueEditor, "ValueChangedAsync", new ChangeEventArgs { Value = "not a number" })!;
        Assert.NotNull(Field(valueEditor, "_error").GetValue(valueEditor));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(saved!));
        await (Task)Invoke(valueEditor, "ValueChangedAsync", new ChangeEventArgs { Value = "true" })!;
        Assert.NotNull(Field(valueEditor, "_error").GetValue(valueEditor));
        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(saved!));
        await (Task)Invoke(valueEditor, "TypeChangedAsync", new ChangeEventArgs { Value = "Boolean" })!;
        Assert.Equal("true", saved);
        await (Task)Invoke(valueEditor, "ValueChangedAsync", new ChangeEventArgs { Value = "false" })!;
        Assert.Equal("false", saved);
        await (Task)Invoke(valueEditor, "TypeChangedAsync", new ChangeEventArgs { Value = "Null" })!;
        Assert.Equal("null", saved);
        Assert.Null(Field(valueEditor, "_error").GetValue(valueEditor));
    }

    [Fact]
    public void NestedEditor_PreservesMalformedDocumentsAndEnforcesItsLimits()
    {
        using var http = new HttpClient(new MappingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var settings = new Dictionary<string, string> { ["mappingJson"] = "{}" };
        var editor = Editor(settings, new MulseApiClient(http));
        Invoke(editor, "AddInputMapping", "/object");
        var field = Assert.Single(((ParsedDataMapViewModel)Field(editor, "_map").GetValue(editor)!).Fields);
        field.SourceScope = "Root"; field.Kind = "Transform"; Invoke(editor, "KindChanged", field);
        Assert.Equal("Root", Assert.Single(field.TransformInputs).SourceScope);
        field.Kind = "Field";
        for (var index = 0; index < 33; index++) Invoke(editor, "AddCondition", field);
        Assert.All(field.Conditions, condition => Assert.Equal("Root", condition.SourceScope));
        Assert.Equal(32, field.Conditions.Count); Assert.Contains("32 conditions", (string)Field(editor, "_error").GetValue(editor)!);
        for (var index = 0; index < 17; index++) Invoke(editor, "AddChainStep", field);
        Assert.Equal(16, field.TransformChain.Count); Assert.Contains("16 chain steps", (string)Field(editor, "_error").GetValue(editor)!);
        field.TransformChain.Clear(); field.Conditions.Clear(); field.Kind = "Object";
        Invoke(editor, "OpenChildren", field);
        for (var index = 0; index < 256; index++) Invoke(editor, "AddInputMapping", "/f" + index);
        Assert.Equal(255, field.Fields.Count);
        Assert.Contains("256 fields", (string)Field(editor, "_error").GetValue(editor)!);
        const string invalid = "{\"fields\":[{\"kind\":\"Object\",\"targetPath\":\"/x\",\"fields\":[null]}]}";
        Invoke(editor, "ReplaceJson", new ChangeEventArgs { Value = invalid });
        Assert.True((bool)Field(editor, "_loadFailed").GetValue(editor)!);
        Assert.Equal(invalid, settings["mappingJson"]);
        Invoke(editor, "ReplaceJson", new ChangeEventArgs { Value = "{}" });
        Assert.False((bool)Field(editor, "_loadFailed").GetValue(editor)!);
        Assert.Empty(((IEnumerable)Field(editor, "_scopes").GetValue(editor)!).Cast<object>());
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

    [Fact]
    public async Task RefreshModules_ExposesPluginTransformsWithoutReplacingCanvasOrMappingEdits()
    {
        var handler = new MappingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var designer = new Designer();
        Property(designer, "ApiClient").SetValue(designer, new MulseApiClient(http));
        Invoke(designer, "BuildFromFlow", View(new PipelineDefinition
        {
            Id = "edited", Augments = [new() { Module = "parsed-data-map-augment", Settings = new() { ["mappingJson"] = "{}" } }]
        }));
        var node = Nodes(designer).Single();
        var settings = (Dictionary<string, string>)Property(node, "Settings").GetValue(node)!;
        settings["mappingJson"] = ParsedDataMappingTests.MapJson(new ParsedDataFieldMap { SourcePath = "/draft", TargetPath = "/kept" });
        var saved = settings["mappingJson"];
        await (Task)Invoke(designer, "RefreshCatalogAsync")!;
        Assert.Empty((IReadOnlyList<ModuleViewModel>)Property(designer, "TransformModules").GetValue(designer)!);
        handler.IncludePlugin = true;
        await (Task)Invoke(designer, "RefreshCatalogAsync")!;
        Assert.Null(Field(designer, "_errorMessage").GetValue(designer));
        Assert.Equal("regex-replace-transform", Assert.Single((IReadOnlyList<ModuleViewModel>)Property(designer, "TransformModules").GetValue(designer)!).Id);
        Assert.Same(node, Nodes(designer).Single());
        Assert.Equal(saved, settings["mappingJson"]);
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
        public bool IncludePlugin;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object result;
            if (request.RequestUri!.AbsolutePath == "/api/modules")
                result = IncludePlugin ? new ModuleViewModel[] { new("regex-replace-transform", "Replace text by pattern", "Transform", "Plugin value transform", [], [], [], []) } : [];
            else if (request.RequestUri.AbsolutePath.EndsWith("/inspect", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadFromJsonAsync<ParsedDataSampleRequestViewModel>(ct);
                result = ParsedDataMapping.Inspect(body!.Text, body.Format, body.RecordPath);
            }
            else if (request.RequestUri.AbsolutePath.EndsWith("/preview", StringComparison.Ordinal))
            {
                var body = await request.Content!.ReadFromJsonAsync<ParsedDataPreviewRequestViewModel>(ct);
                result = new ParsedDataPreviewViewModel(await ParsedDataMapping.MapAsync(body!.MappingJson, body.ParsedData, body.Format, ct,
                    ParsedDataMappingTests.TransformInvoker(), ParsedDataMappingTests.Context));
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
