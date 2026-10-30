using System.Net;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Mapping;

namespace Service.Tests;

public sealed class XmlMappingVisualDesignerTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    internal static XmlSchemaTreeViewModel Tree(string schema)
        => JsonSerializer.Deserialize<XmlSchemaTreeViewModel>(JsonSerializer.Serialize(XmlSchemaInspector.Analyze([schema]), Options), Options)!;
    internal static XmlSchemaSelectionViewModel Select(XmlSchemaTreeViewModel tree, string localPath)
    {
        var root = tree.Roots.Single();
        return new(root, root.Fields.Single(field => field.LocalPath == localPath), tree.Namespaces);
    }

    [Fact]
    public void Graph_ResolvesNamespaceAliasesAndRetainsHierarchyAttributesAndUnsupportedFields()
    {
        var map = new XmlMappingViewModel
        {
            Namespaces = new() { ["s"] = "urn:source", ["t"] = "urn:target" }, TargetRoot = "t:Result",
            Fields = [new() { SourcePath = "/s:Order/s:Id", TargetPath = "Number" }, new() { SourcePath = "/s:Order/@code", TargetPath = "@code" }]
        };
        var sources = XmlMappingGraph.Nodes(map, Tree(NativeMapImportTests.SourceXsd), true);
        var id = Assert.Single(sources, node => node.Key == XmlMappingGraph.Key("/s:Order/s:Id", map.Namespaces));
        Assert.Equal(1, id.Depth);
        Assert.True(id.CanConnect);
        Assert.NotNull(id.Selection);
        Assert.False(sources.Single(node => node.Path.EndsWith(":Item", StringComparison.Ordinal)).CanConnect);
        Assert.False(sources[0].CanConnect);
        Assert.Contains(sources, node => node.Path.EndsWith("/@code", StringComparison.Ordinal) && node.CanConnect);
        var targets = XmlMappingGraph.Nodes(map, Tree(NativeMapImportTests.TargetXsd), false);
        Assert.Single(targets, node => node.Key == XmlMappingGraph.Key("/t:Result/Number", map.Namespaces));
        Assert.NotEqual(XmlMappingGraph.Key("/t:Result/Number", map.Namespaces), XmlMappingGraph.Key("/t:Result/t:Number", map.Namespaces));
        Assert.NotEqual(XmlMappingGraph.Key("/s:Order/@code", map.Namespaces), XmlMappingGraph.Key("/s:Order/code", map.Namespaces));
        Assert.Equal(XmlMappingGraph.Key("", map.Namespaces), XmlMappingGraph.Key(null, map.Namespaces));
        var reordered = new XmlMappingViewModel
        {
            Fields = [new() { SourcePath = "/Input/A/First" }, new() { SourcePath = "/Input/B/Value" }, new() { SourcePath = "/Input/A/Last" }]
        };
        Assert.Equal(new[] { "/Input", "/Input/A", "/Input/A/First", "/Input/A/Last", "/Input/B", "/Input/B/Value" },
            XmlMappingGraph.Nodes(reordered, null, true).Select(node => node.Path));
    }

    [Fact]
    public async Task ReopenedMap_RendersSavedTreesLinesConstantsAndOperationNodesWithoutMutatingDefinition()
    {
        var map = new XmlMappingViewModel
        {
            Version = 2, TargetRoot = "t:Result", Namespaces = new() { ["s"] = "urn:source", ["t"] = "urn:target" },
            SourceSchemas = [NativeMapImportTests.SourceXsd], TargetSchemas = [NativeMapImportTests.TargetXsd],
            Fields = [new() { SourcePath = "/s:Order/s:Id", TargetPath = "Number", Operations = [new() { Kind = "UppercaseAscii" }, new() { Kind = "Prefix", Argument = "id:" }] },
                new() { Constant = "<&constant>", TargetPath = "Text" }]
        };
        var original = JsonSerializer.Serialize(map, Options);
        var handler = new SchemaHandler();
        var html = await Render(map, handler);
        Assert.Equal(2, handler.Requests);
        Assert.Contains("Visual mapping designer", html);
        Assert.Contains("Connect fields", html);
        Assert.Contains("source-node", html);
        Assert.Contains("target-node", html);
        Assert.Contains("<path", html);
        Assert.Contains("UppercaseAscii", html);
        Assert.Contains("Prefix", html);
        Assert.Contains("id:", html);
        Assert.Contains("&lt;&amp;constant&gt;", html);
        Assert.DoesNotContain("Constant: <&constant>", html);
        Assert.Contains("Add operation", html);
        Assert.Contains("Remove connection", html);
        Assert.Contains("Repeating record", html);
        Assert.Equal(original, JsonSerializer.Serialize(map, Options));
    }

    [Fact]
    public async Task SavedSchemaFailure_IsVisibleAndLeavesExistingConnectionsIntact()
    {
        var map = new XmlMappingViewModel { SourceSchemas = ["<bad>"], Fields = [new() { SourcePath = "/Input/Value", TargetPath = "Value" }] };
        var original = JsonSerializer.Serialize(map, Options);
        var html = await Render(map, new SchemaHandler { Fail = true });
        Assert.Contains("Unable to load saved schema trees", html);
        Assert.Contains("Saved mappings are preserved", html);
        Assert.Contains("Mapping connection to Value", html);
        Assert.Equal(original, JsonSerializer.Serialize(map, Options));
    }

    [Fact]
    public async Task VisualConnect_CallsExistingEditorAndRejectsDuplicateOverlappingRepeatedAndWrongRootAssignments()
    {
        var editor = new XmlMappingEditor();
        Set(editor, "Value", "{\"fields\":[]}");
        Invoke(editor, "OnParametersSet");
        var source = Tree(NativeMapImportTests.SourceXsd);
        var target = Tree(NativeMapImportTests.TargetXsd);
        var visual = new XmlMappingVisualDesigner();
        Set(visual, "Connected", EventCallback.Factory.Create<XmlSchemaFieldPairViewModel>(new object(), pair => (Task)Invoke(editor, "AddSchemaFieldAsync", pair)!));
        SetField(visual, "_source", Select(source, "/Order/Id"));
        SetField(visual, "_target", Select(target, "/Result/Number"));
        await (Task)Invoke(visual, "ConnectAsync")!;
        var map = (XmlMappingViewModel)GetField(editor, "_map")!;
        Assert.Equal("/sn1:Order/sn1:Id", Assert.Single(map.Fields).SourcePath);
        Assert.Equal("Number", map.Fields[0].TargetPath);
        var original = editor.Value;
        await (Task)Invoke(visual, "ConnectAsync")!;
        Assert.Equal(original, editor.Value);
        Assert.Contains("already has a connection", (string)GetField(editor, "_error")!);
        SetField(visual, "_source", Select(source, "/Order/Item"));
        SetField(visual, "_target", Select(target, "/Result/Text"));
        await (Task)Invoke(visual, "ConnectAsync")!;
        Assert.Equal(original, editor.Value);
        Assert.Contains("scalar", (string)GetField(editor, "_error")!);
        SetField(visual, "_source", Select(source, "/Order/Optional"));
        await (Task)Invoke(visual, "ConnectAsync")!;
        Assert.Equal(2, map.Fields.Count);
        Assert.Equal("Omit", map.Fields[1].MissingValueBehavior);
        Assert.Equal(2, map.Version);
        var output = System.Xml.Linq.XDocument.Parse(new Mulse.CompatibilityPack.XmlMappingEngine(editor.Value).Transform("<Order xmlns='urn:source'><Id>value</Id></Order>"));
        Assert.Equal("value", output.Root!.Element("Number")!.Value);
        Assert.Null(output.Root.Element("Text"));
        var overlap = Select(target, "/Result/Number") with { Field = new("/n1:Result/Number/Child", "/Result/Number/Child", true, true, false, false, null) };
        var before = editor.Value;
        await (Task)Invoke(editor, "AddSchemaFieldAsync", new XmlSchemaFieldPairViewModel(Select(source, "/Order/Id"), overlap))!;
        Assert.Equal(before, editor.Value);
        Assert.Contains("overlaps", (string)GetField(editor, "_error")!);
        var wrongRoot = target.Roots.Single() with { Path = "/n1:Different" };
        await (Task)Invoke(editor, "AddSchemaFieldAsync", new XmlSchemaFieldPairViewModel(Select(source, "/Order/Id"),
            new(wrongRoot, new("/n1:Different/Text", "/Different/Text", true, true, false, false, null), target.Namespaces)))!;
        Assert.Equal(before, editor.Value);
        Assert.Contains("conflict", (string)GetField(editor, "_error")!);
        await (Task)Invoke(editor, "RemoveFieldAsync", map.Fields[1])!;
        Assert.Single(map.Fields);
    }

    [Fact]
    public async Task ReplacingInspectedTrees_ClearsSelectionsAndSchemaToolsPublishInvalidation()
    {
        using var http = new HttpClient(new SchemaHandler()) { BaseAddress = new("http://localhost/") };
        var tools = new XmlMappingSourceTools();
        Set(tools, "ApiClient", new MulseApiClient(http));
        XmlMappingTreesViewModel? published = null;
        Set(tools, "TreesChanged", EventCallback.Factory.Create<XmlMappingTreesViewModel>(new object(), trees => published = trees));
        await (Task)Invoke(tools, "SetText", NativeMapImportTests.SourceXsd, true)!;
        await (Task)Invoke(tools, "AnalyzeAsync", true)!;
        Assert.NotNull(published!.Source);
        var visual = new XmlMappingVisualDesigner();
        Set(visual, "ApiClient", new MulseApiClient(http));
        Set(visual, "SourceTree", published.Source);
        await (Task)Invoke(visual, "OnParametersSetAsync")!;
        SetField(visual, "_source", Select(published.Source!, "/Order/Id"));
        await (Task)Invoke(tools, "SetText", "changed", true)!;
        Assert.Null(published.Source);
        Set(visual, "SourceTree", published.Source);
        await (Task)Invoke(visual, "OnParametersSetAsync")!;
        Assert.Null(GetField(visual, "_source"));
    }

    [Fact]
    public async Task SavedSchemaReload_DiscardsStaleResponsesAndAllowsExplicitRetry()
    {
        var handler = new SchemaHandler { DelayFirst = true };
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var visual = new XmlMappingVisualDesigner();
        Set(visual, "ApiClient", new MulseApiClient(http));
        Set(visual, "Map", new XmlMappingViewModel { SourceSchemas = [NativeMapImportTests.SourceXsd] });
        var oldRequest = (Task)Invoke(visual, "OnParametersSetAsync")!;
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Set(visual, "Map", new XmlMappingViewModel { SourceSchemas = [NativeMapImportTests.TargetXsd] });
        await (Task)Invoke(visual, "OnParametersSetAsync")!;
        handler.ReleaseFirst.SetResult(true);
        await oldRequest;
        var tree = Assert.IsType<XmlSchemaTreeViewModel>(GetField(visual, "_savedSource"));
        Assert.Contains("Result", tree.Roots.Single().Name);
        handler.Fail = true;
        await (Task)Invoke(visual, "ReloadAsync")!;
        Assert.Contains("Unable to load saved schema trees", (string)GetField(visual, "_error")!);
        handler.Fail = false;
        await (Task)Invoke(visual, "ReloadAsync")!;
        Assert.Null(GetField(visual, "_error"));
        Assert.NotNull(GetField(visual, "_savedSource"));
    }

    [Fact]
    public async Task FieldLimitAndSourceRootAssertion_RejectVisualChangesWithoutMutatingSavedJson()
    {
        var editor = new XmlMappingEditor();
        Set(editor, "Value", "{\"version\":2,\"sourceRoot\":\"Different\",\"fields\":[]}");
        Invoke(editor, "OnParametersSet");
        var source = Tree(NativeMapImportTests.SourceXsd);
        var target = Tree(NativeMapImportTests.TargetXsd);
        var pair = new XmlSchemaFieldPairViewModel(Select(source, "/Order/Id"), Select(target, "/Result/Number"));
        var original = editor.Value;
        await (Task)Invoke(editor, "AddSchemaFieldAsync", pair)!;
        Assert.Equal(original, editor.Value);
        Assert.Contains("conflict", (string)GetField(editor, "_error")!);
        var model = (XmlMappingViewModel)GetField(editor, "_map")!;
        model.SourceRoot = null;
        model.Fields.AddRange(Enumerable.Range(0, 128).Select(index => new XmlFieldMappingViewModel { TargetPath = "Value" + index, Constant = "value" }));
        await (Task)Invoke(editor, "WriteAsync")!;
        original = editor.Value;
        await (Task)Invoke(editor, "AddSchemaFieldAsync", pair)!;
        Assert.Equal(original, editor.Value);
        Assert.Contains("128", (string)GetField(editor, "_error")!);
        await (Task)Invoke(editor, "AddFieldAsync")!;
        Assert.Equal(original, editor.Value);
        Assert.Equal(128, model.Fields.Count);
        Set(editor, "Value", JsonSerializer.Serialize(new XmlMappingViewModel { Namespaces = Enumerable.Range(0, 32).ToDictionary(index => "p" + index, index => "urn:test:" + index) }, Options));
        Invoke(editor, "OnParametersSet");
        original = editor.Value;
        await (Task)Invoke(editor, "AddSchemaFieldAsync", pair)!;
        Assert.Equal(original, editor.Value);
        Assert.Contains("32 prefixes", (string)GetField(editor, "_error")!);
        Assert.Equal(3, XmlMappingGraph.Key("/s:Root/s:Id", new Dictionary<string, string> { ["s"] = "urn:x}/{}Name" }).Split('/').Length);
    }

    private static async Task<string> Render(XmlMappingViewModel map, SchemaHandler handler)
    {
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<XmlMappingVisualDesigner>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(XmlMappingVisualDesigner.Map)] = map }))).ToHtmlString());
    }

    private sealed class SchemaHandler : HttpMessageHandler
    {
        public bool Fail { get; set; }
        public bool DelayFirst { get; init; }
        public TaskCompletionSource<bool> FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("/api/flow-designer/xml-mapping/schemas", request.RequestUri!.AbsolutePath);
            var count = ++Requests;
            if (Fail) return new(HttpStatusCode.BadRequest) { Content = System.Net.Http.Json.JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = "Invalid schema" }) };
            var body = JsonSerializer.Deserialize<XmlSchemaAnalysisRequestViewModel>(await request.Content!.ReadAsStringAsync(cancellationToken), Options)!;
            if (DelayFirst && count == 1)
            {
                FirstStarted.SetResult(true);
                await ReleaseFirst.Task.WaitAsync(cancellationToken);
            }
            return new(HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(XmlSchemaInspector.Analyze(body.Schemas)) };
        }
    }

    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.SetValue(target, value);
    private static void SetField(object target, string name, object? value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? GetField(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
}
