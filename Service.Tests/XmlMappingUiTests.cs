using System.Collections;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Models;

namespace Service.Tests;

public sealed class XmlMappingUiTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditorChanges_SurviveDesignerCreateUpdateAndReopenForManualAndImportedDrafts(bool imported)
    {
        var pipeline = XmlMappingTests.Pipeline();
        if (imported)
            pipeline = FlowMappings.CopyPipeline(pipeline, pipeline.Id, false, null, new("BizTalk", "test.btproj", null, [], [], []));
        var handler = new SavingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var designer = new Designer();
        Property(designer, "ApiClient").SetValue(designer, new MulseApiClient(http));
        Property(designer, "Navigation").SetValue(designer, new TestNavigation());
        Invoke(designer, "BuildFromFlow", View(pipeline));
        var settings = RenderSettings(designer);
        var editor = new XmlMappingEditor();
        Property(editor, "Value").SetValue(editor, settings["mappingJson"]);
        Property(editor, "ValueChanged").SetValue(editor, EventCallback.Factory.Create<string>(new object(), value => settings["mappingJson"] = value));
        Invoke(editor, "OnParametersSet");
        await (Task)Invoke(editor, "AddFieldAsync")!;
        var model = (XmlMappingViewModel)Field(editor, "_map").GetValue(editor)!;
        var added = model.Fields.Last();
        await (Task)Invoke(editor, "SetKindAsync", added, "Constant")!;
        added.Constant = "from UI";
        added.TargetPath = "t:EditorAdded";
        await (Task)Invoke(editor, "WriteAsync")!;
        await (Task)Invoke(editor, "AddFieldAsync")!;
        var optional = model.Fields.Last();
        optional.SourcePath = "/s:Order/s:Absent";
        optional.TargetPath = "t:Defaulted";
        await (Task)Invoke(editor, "SetMissingBehaviorAsync", optional, "Default")!;
        optional.DefaultValue = "default from UI";
        await (Task)Invoke(editor, "WriteAsync")!;
        Assert.Equal(2, model.Version);
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field(designer, "_errorMessage").GetValue(designer));
        Assert.Equal(1, handler.Saves);
        Assert.Equal(settings["mappingJson"], handler.Saved!.Deliveries[0].Render.Settings["mappingJson"]);
        Invoke(designer, "BuildFromFlow", View(handler.Saved));
        Assert.Equal(editor.Value, RenderSettings(designer)["mappingJson"]);
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field(designer, "_errorMessage").GetValue(designer));
        Assert.Equal(2, handler.Saves);
        Assert.Equal(editor.Value, handler.Saved!.Deliveries[0].Render.Settings["mappingJson"]);
        Assert.Equal(imported, handler.Saved.Migration is not null);
        if (imported) Assert.False(handler.Saved.Enabled);
    }

    [Fact]
    public async Task MappingPanel_ExposesEditorAndNativeRenderCreationWithoutUnsavedGlobalTable()
    {
        using var http = new HttpClient(new SavingHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).AddSingleton<NavigationManager>(new TestNavigation())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            Designer? designer = null;
            var rendered = await renderer.RenderComponentAsync<DesignerHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(DesignerHost.Capture)] = (Action<Designer>)(value => designer = value)
            }));
            Assert.NotNull(designer);
            Invoke(designer, "ToggleMappingDesigner");
            await designer.SetParametersAsync(ParameterView.Empty);
            var html = rendered.ToHtmlString();
            Assert.Contains("Add XML mapping render", html);
            Assert.Contains("XML mapping editor", html);
            Assert.Contains("XML source path", html);
            Assert.Contains("XML target path", html);
            Assert.Contains("Text constant", html);
            Assert.Contains("Add namespace", html);
            Assert.Contains("Preview XML mapping", html);
            Assert.Contains("not saved", html);
            Assert.Contains("XSD schema trees and BizTalk map import", html);
            Assert.Contains("Inspect source schema tree", html);
            Assert.Contains("Create native map proposal", html);
            Assert.DoesNotContain("Mapping literal JSON", html);
            Invoke(designer, "AddXmlMappingRender");
            await designer.SetParametersAsync(ParameterView.Empty);
            var nodes = ((IEnumerable)Field(designer, "_nodes").GetValue(designer)!).Cast<object>();
            Assert.Equal(2, nodes.Count(node => (string)Property(node, "ModuleId").GetValue(node)! == "xml-mapping-render"));
            Assert.Contains("add a deliver node before saving", rendered.ToHtmlString());
        });
    }

    [Theory]
    [InlineData("{\"version\":9,\"fields\":[]}")]
    [InlineData("{\"version\":1,\"futureProperty\":42}")]
    [InlineData("{\"version\":1,\"fields\":[null]}")]
    [InlineData("{")]
    public async Task UnsupportedOrMalformedJson_IsPreservedAndShowsAnError(string json)
    {
        using var http = new HttpClient(new SavingHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http))
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var editor = new XmlMappingEditor();
        Property(editor, "Value").SetValue(editor, json);
        Invoke(editor, "OnParametersSet");
        Assert.Equal(json, editor.Value);
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<XmlMappingEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(XmlMappingEditor.Value)] = json }))).ToHtmlString());
        Assert.Contains("original definition is preserved", html);
        Assert.Contains("role=\"alert\"", html);
        Assert.DoesNotContain("Add XML field mapping", html);
    }

    [Fact]
    public async Task EditorPreview_DisplaysOutputClearsItAfterEditsAndSurfacesFailure()
    {
        var handler = new SavingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var editor = new XmlMappingEditor();
        Property(editor, "ApiClient").SetValue(editor, new MulseApiClient(http));
        Property(editor, "Value").SetValue(editor, XmlMappingTests.Json());
        Invoke(editor, "OnParametersSet");
        Field(editor, "_sample").SetValue(editor, XmlMappingTests.Sample);
        await (Task)Invoke(editor, "PreviewAsync")!;
        Assert.Null(Field(editor, "_error").GetValue(editor));
        Assert.Equal(new Mulse.CompatibilityPack.XmlMappingEngine(XmlMappingTests.Json()).Transform(XmlMappingTests.Sample),
            Assert.IsType<XmlMappingPreviewViewModel>(Field(editor, "_preview").GetValue(editor)).OutputXml);
        await (Task)Invoke(editor, "WriteAsync")!;
        Assert.Null(Field(editor, "_preview").GetValue(editor));
        Field(editor, "_sample").SetValue(editor, "<wrong />");
        await (Task)Invoke(editor, "PreviewAsync")!;
        Assert.Contains("exactly one scalar", Assert.IsType<string>(Field(editor, "_error").GetValue(editor)));
        Assert.Null(Field(editor, "_preview").GetValue(editor));
        Assert.Equal(0, handler.Saves);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeProposal_IsAppliedExplicitlyAndSurvivesSaveReopenWithoutChangingProvenance(bool imported)
    {
        var pipeline = XmlMappingTests.Pipeline();
        if (imported) pipeline = FlowMappings.CopyPipeline(pipeline, pipeline.Id, false, null, new("BizTalk", "test.btproj", null, [], [], []));
        var handler = new SavingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var client = new MulseApiClient(http);
        var designer = new Designer();
        Property(designer, "ApiClient").SetValue(designer, client);
        Property(designer, "Navigation").SetValue(designer, new TestNavigation());
        Invoke(designer, "BuildFromFlow", View(pipeline));
        var settings = RenderSettings(designer);
        var original = settings["mappingJson"];
        var tools = new XmlMappingSourceTools();
        Property(tools, "ApiClient").SetValue(tools, client);
        Field(tools, "_sourceXsd").SetValue(tools, NativeMapImportTests.SourceXsd);
        Field(tools, "_targetXsd").SetValue(tools, NativeMapImportTests.TargetXsd);
        Field(tools, "_mapXml").SetValue(tools, NativeMapImportTests.Map());
        var editor = new XmlMappingEditor();
        Property(editor, "Value").SetValue(editor, original);
        Property(editor, "ValueChanged").SetValue(editor, EventCallback.Factory.Create<string>(new object(), value => settings["mappingJson"] = value));
        Invoke(editor, "OnParametersSet");
        Property(tools, "Applied").SetValue(tools, EventCallback.Factory.Create<string>(new object(), value => (Task)Invoke(editor, "SetRawAsync", value)!));
        await (Task)Invoke(tools, "ConvertAsync")!;
        var proposal = Assert.IsType<NativeMapConversionViewModel>(Field(tools, "_proposal").GetValue(tools));
        Assert.Equal(original, settings["mappingJson"]);
        await tools.Applied.InvokeAsync(proposal.MappingJson);
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field(designer, "_errorMessage").GetValue(designer));
        Assert.Equal(imported, handler.Saved!.Migration is not null);
        if (imported) Assert.False(handler.Saved.Enabled);
        Invoke(designer, "BuildFromFlow", View(handler.Saved));
        Assert.Equal(proposal.MappingJson, RenderSettings(designer)["mappingJson"]);
        var reopened = JsonSerializer.Deserialize<XmlMappingViewModel>(RenderSettings(designer)["mappingJson"], Options)!;
        Assert.Equal("sn1:Order", reopened.SourceRoot);
        Assert.Equal(NativeMapImportTests.SourceXsd, Assert.Single(reopened.SourceSchemas));
        Assert.Equal(NativeMapImportTests.TargetXsd, Assert.Single(reopened.TargetSchemas));
        Field(tools, "_mapXml").SetValue(tools, NativeMapImportTests.Map("Order/Item"));
        await (Task)Invoke(tools, "ConvertAsync")!;
        Assert.Null(Field(tools, "_proposal").GetValue(tools));
        Assert.Contains("existing mapping preserved", Assert.IsType<string>(Field(tools, "_error").GetValue(tools)));
        Assert.Equal(proposal.MappingJson, RenderSettings(designer)["mappingJson"]);
    }

    [Fact]
    public async Task SchemaSelections_CreateNamedPathsAndRefuseNamespaceConflicts()
    {
        using var http = new HttpClient(new SavingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var tools = new XmlMappingSourceTools();
        Property(tools, "ApiClient").SetValue(tools, new MulseApiClient(http));
        Field(tools, "_sourceXsd").SetValue(tools, NativeMapImportTests.SourceXsd);
        Field(tools, "_targetXsd").SetValue(tools, NativeMapImportTests.TargetXsd);
        await (Task)Invoke(tools, "AnalyzeAsync", true)!;
        await (Task)Invoke(tools, "AnalyzeAsync", false)!;
        var source = Assert.IsType<XmlSchemaTreeViewModel>(Field(tools, "_sourceTree").GetValue(tools));
        var target = Assert.IsType<XmlSchemaTreeViewModel>(Field(tools, "_targetTree").GetValue(tools));
        var sourceSelection = new XmlSchemaSelectionViewModel(source.Roots.Single(), source.Roots.Single().Fields.Single(field => field.LocalPath == "/Order/Id"), source.Namespaces);
        var targetSelection = new XmlSchemaSelectionViewModel(target.Roots.Single(), target.Roots.Single().Fields.Single(field => field.LocalPath == "/Result/Number"), target.Namespaces);
        var editor = new XmlMappingEditor();
        await (Task)Invoke(editor, "AddSchemaFieldAsync", new XmlSchemaFieldPairViewModel(sourceSelection, targetSelection))!;
        var map = JsonSerializer.Deserialize<XmlMappingViewModel>(editor.Value, Options)!;
        Assert.Equal("tn1:Result", map.TargetRoot);
        Assert.Equal("/sn1:Order/sn1:Id", Assert.Single(map.Fields).SourcePath);
        Assert.Equal("Number", Assert.Single(map.Fields).TargetPath);
        var original = editor.Value;
        sourceSelection = sourceSelection with { Namespaces = new Dictionary<string, string> { ["n1"] = "urn:different" } };
        await (Task)Invoke(editor, "AddSchemaFieldAsync", new XmlSchemaFieldPairViewModel(sourceSelection, targetSelection))!;
        Assert.Equal(original, editor.Value);
        Assert.Contains("conflict", Assert.IsType<string>(Field(editor, "_error").GetValue(editor)));
        Invoke(tools, "SetText", "changed", true);
        Assert.Null(Field(tools, "_sourceTree").GetValue(tools));
    }

    [Fact]
    public async Task RuntimeSchemas_AreAttachedOnlyByExplicitActionAndCanBeRemovedWithoutLosingFields()
    {
        using var http = new HttpClient(new SavingHandler()) { BaseAddress = new Uri("http://localhost/") };
        var tools = new XmlMappingSourceTools();
        Property(tools, "ApiClient").SetValue(tools, new MulseApiClient(http));
        Field(tools, "_sourceXsd").SetValue(tools, NativeMapImportTests.SourceXsd);
        Field(tools, "_targetXsd").SetValue(tools, NativeMapImportTests.TargetXsd);
        var editor = new XmlMappingEditor();
        Property(editor, "Value").SetValue(editor, XmlMappingTests.Json());
        Invoke(editor, "OnParametersSet");
        Property(tools, "SchemasApplied").SetValue(tools, EventCallback.Factory.Create<XmlMappingSchemasViewModel>(new object(), schemas => (Task)Invoke(editor, "SetSchemasAsync", schemas)!));
        await (Task)Invoke(tools, "AnalyzeAsync", true)!;
        Assert.Empty(JsonSerializer.Deserialize<XmlMappingViewModel>(editor.Value, Options)!.SourceSchemas);
        await (Task)Invoke(tools, "AttachSchemasAsync")!;
        var attached = JsonSerializer.Deserialize<XmlMappingViewModel>(editor.Value, Options)!;
        Assert.Equal(2, attached.Version);
        Assert.Equal(NativeMapImportTests.SourceXsd, Assert.Single(attached.SourceSchemas));
        Assert.Equal(NativeMapImportTests.TargetXsd, Assert.Single(attached.TargetSchemas));
        var valid = editor.Value;
        Field(tools, "_targetXsd").SetValue(tools, "<invalid>");
        await (Task)Invoke(tools, "AttachSchemasAsync")!;
        Assert.Equal(valid, editor.Value);
        Assert.Contains("existing mapping preserved", Assert.IsType<string>(Field(tools, "_error").GetValue(tools)));
        await (Task)Invoke(editor, "SetSchemasAsync", new XmlMappingSchemasViewModel([], []))!;
        var removed = JsonSerializer.Deserialize<XmlMappingViewModel>(editor.Value, Options)!;
        Assert.Empty(removed.SourceSchemas);
        Assert.Empty(removed.TargetSchemas);
        Assert.Equal(JsonSerializer.Serialize(attached.Fields, Options), JsonSerializer.Serialize(removed.Fields, Options));
        Assert.Equal(attached.TargetRoot, removed.TargetRoot);
    }

    private static FlowViewModel View(PipelineDefinition pipeline)
        => JsonSerializer.Deserialize<FlowViewModel>(JsonSerializer.Serialize(FlowMappings.MapFlow(pipeline), Options), Options)!;
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static PropertyInfo Property(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static Dictionary<string, string> RenderSettings(Designer designer)
    {
        var render = ((IEnumerable)Field(designer, "_nodes").GetValue(designer)!).Cast<object>().Single(node => Property(node, "Stage").GetValue(node)!.ToString() == "Render");
        return (Dictionary<string, string>)Property(render, "Settings").GetValue(render)!;
    }

    private sealed class DesignerHost : ComponentBase
    {
        [Parameter] public Action<Designer>? Capture { get; set; }
        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenComponent<Designer>(0);
            builder.AddAttribute(1, nameof(Designer.FlowId), "xml-map");
            builder.AddComponentReferenceCapture(2, value => Capture?.Invoke((Designer)value));
            builder.CloseComponent();
        }
    }
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
    private sealed class SavingHandler : HttpMessageHandler
    {
        public PipelineDefinition? Saved { get; private set; }
        public int Saves { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload;
            if (request.RequestUri!.AbsolutePath == "/api/flow-designer/xml-mapping/schemas")
            {
                var body = JsonSerializer.Deserialize<XmlSchemaAnalysisRequestViewModel>(await request.Content!.ReadAsStringAsync(cancellationToken), Options)!;
                payload = Service.Mapping.XmlSchemaInspector.Analyze(body.Schemas);
            }
            else if (request.RequestUri.AbsolutePath == "/api/flow-designer/xml-mapping/import")
            {
                var body = JsonSerializer.Deserialize<XmlMappingImportRequestViewModel>(await request.Content!.ReadAsStringAsync(cancellationToken), Options)!;
                try { payload = Service.Mapping.BizTalkNativeMapConverter.Convert(body.MapXml, body.SourceSchemas, body.TargetSchemas); }
                catch (ArgumentException exception)
                {
                    return new(HttpStatusCode.BadRequest) { Content = System.Net.Http.Json.JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = exception.Message }) };
                }
            }
            else if (request.RequestUri.AbsolutePath == "/api/flow-designer/xml-mapping/preview")
            {
                var preview = JsonSerializer.Deserialize<XmlMappingPreviewRequestViewModel>(await request.Content!.ReadAsStringAsync(cancellationToken), Options)!;
                try
                {
                    var engine = new Mulse.CompatibilityPack.XmlMappingEngine(preview.MappingJson);
                    payload = new XmlMappingPreviewViewModel(engine.Transform(preview.SampleXml), engine.Xslt);
                }
                catch (ArgumentException exception)
                {
                    return new(HttpStatusCode.BadRequest) { Content = System.Net.Http.Json.JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails { Detail = exception.Message }) };
                }
            }
            else if (request.RequestUri.AbsolutePath is "/api/config-values/usages" or "/api/config-values/references") payload = Array.Empty<string>();
            else if (request.RequestUri.AbsolutePath == "/api/modules") payload = new ModuleViewModel[] { new("xml-mapping-render", "XML mapping render", "Render", "XML maps", [], [], [], []) };
            else if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put)
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                Saved = request.Method == HttpMethod.Post
                    ? FlowMappings.MapPipeline(JsonSerializer.Deserialize<CreateFlowRequest>(json, Options)!)
                    : FlowMappings.MapPipeline(JsonSerializer.Deserialize<UpdateFlowRequest>(json, Options)!);
                Saves++;
                payload = FlowMappings.MapFlow(Saved);
            }
            else if (request.RequestUri.AbsolutePath.StartsWith("/api/flows/", StringComparison.Ordinal)) payload = FlowMappings.MapFlow(Saved ?? XmlMappingTests.Pipeline());
            else throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload, payload.GetType(), Options), System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
