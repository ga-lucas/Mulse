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
        await (Task)Invoke(editor, "AddOperationAsync", added)!;
        var operation = added.Operations.Single();
        await (Task)Invoke(editor, "SetOperationKindAsync", operation, "Prefix")!;
        operation.Argument = "id:";
        await (Task)Invoke(editor, "AddOperationAsync", added)!;
        var uppercase = added.Operations.Last();
        await (Task)Invoke(editor, "SetOperationKindAsync", uppercase, "UppercaseAscii")!;
        await (Task)Invoke(editor, "MoveOperationAsync", added, uppercase, -1)!;
        Assert.Equal(new[] { "UppercaseAscii", "Prefix" }, added.Operations.Select(entry => entry.Kind));
        var visual = new XmlMappingVisualDesigner();
        Property(visual, "Connected").SetValue(visual, EventCallback.Factory.Create<XmlSchemaFieldPairViewModel>(new object(), pair => (Task)Invoke(editor, "AddSchemaFieldAsync", pair)!));
        var sourceTree = XmlMappingVisualDesignerTests.Tree(NativeMapImportTests.SourceXsd);
        var targetTree = XmlMappingVisualDesignerTests.Tree(NativeMapImportTests.TargetXsd);
        Field(visual, "_source").SetValue(visual, XmlMappingVisualDesignerTests.Select(sourceTree, "/Order/Id"));
        Field(visual, "_target").SetValue(visual, XmlMappingVisualDesignerTests.Select(targetTree, "/Result/Text"));
        await (Task)Invoke(visual, "ConnectAsync")!;
        Assert.Equal("Text", model.Fields.Last().TargetPath);
        Assert.Equal("/s:Order/s:Id", model.Fields.Last().SourcePath);
        Assert.Equal(2, model.Version);
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field(designer, "_errorMessage").GetValue(designer));
        Assert.Equal(1, handler.Saves);
        Assert.Equal(settings["mappingJson"], handler.Saved!.Deliveries[0].Render.Settings["mappingJson"]);
        Invoke(designer, "BuildFromFlow", View(handler.Saved));
        Assert.Equal(editor.Value, RenderSettings(designer)["mappingJson"]);
        var output = new Mulse.CompatibilityPack.XmlMappingEngine(RenderSettings(designer)["mappingJson"]).Transform(XmlMappingTests.Sample);
        Assert.Equal("id:FROM UI", System.Xml.Linq.XDocument.Parse(output).Root!.Element(System.Xml.Linq.XName.Get("EditorAdded", "urn:target"))!.Value);
        Assert.Equal("42", System.Xml.Linq.XDocument.Parse(output).Root!.Element("Text")!.Value);
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
        var components = new TrackingActivator();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(components)
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
            Assert.Contains("aria-label=\"Designer workspace\"", rendered.ToHtmlString());
            Assert.DoesNotContain("XML mapping editor", rendered.ToHtmlString());
            Invoke(designer, "SetMappingWorkspace", true);
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
            Assert.Contains("Add string operation", html);
            Assert.Contains("ASCII case conversion", html);
            Assert.Contains("id=\"flow-workspace\" class=\"flow-workspace\" role=\"tabpanel\" aria-labelledby=\"flow-workspace-tab\" hidden", html);
            Assert.DoesNotContain("floating-panel-title\">Mapping Designer", html);
            Assert.Contains("role=\"tooltip\"", html);
            Assert.DoesNotContain("Mapping literal JSON", html);
            var editor = Assert.Single(components.Created.OfType<XmlMappingEditor>());
            var tools = Assert.Single(components.Created.OfType<XmlMappingSourceTools>());
            designer.OnDockSplitChanged(63);
            await (Task)Invoke(tools, "SetText", NativeMapImportTests.SourceXsd, true)!;
            Field(editor, "_sample").SetValue(editor, XmlMappingTests.Sample);
            await (Task)Invoke(editor, "PreviewAsync")!;
            var preview = Assert.IsType<XmlMappingPreviewViewModel>(Field(editor, "_preview").GetValue(editor));
            var before = RenderSettings(designer)["mappingJson"];
            Invoke(designer, "SetMappingWorkspace", false);
            await designer.SetParametersAsync(ParameterView.Empty);
            var flowHtml = rendered.ToHtmlString();
            Assert.Contains("id=\"mapping-workspace\" class=\"mapping-workspace\" role=\"tabpanel\" aria-labelledby=\"mapping-workspace-tab\" hidden", flowHtml);
            Assert.Contains("XML mapping editor", flowHtml);
            Invoke(designer, "SetMappingWorkspace", true);
            await designer.SetParametersAsync(ParameterView.Empty);
            Assert.Equal(before, RenderSettings(designer)["mappingJson"]);
            Assert.Same(editor, Assert.Single(components.Created.OfType<XmlMappingEditor>()));
            Assert.Same(tools, Assert.Single(components.Created.OfType<XmlMappingSourceTools>()));
            Assert.Equal(NativeMapImportTests.SourceXsd, Field(tools, "_sourceXsd").GetValue(tools));
            Assert.Equal(XmlMappingTests.Sample, Field(editor, "_sample").GetValue(editor));
            Assert.Same(preview, Field(editor, "_preview").GetValue(editor));
            Assert.Equal(63d, Field(designer, "_toolboxShare").GetValue(designer));
            Assert.Contains("aria-valuenow=\"63\"", rendered.ToHtmlString());
            Invoke(designer, "AddXmlMappingRender");
            await designer.SetParametersAsync(ParameterView.Empty);
            var nodes = ((IEnumerable)Field(designer, "_nodes").GetValue(designer)!).Cast<object>();
            var maps = nodes.Where(node => (string)Property(node, "ModuleId").GetValue(node)! == "xml-mapping-render").ToArray();
            Assert.Equal(2, maps.Length);
            Assert.Equal(Property(maps.Last(), "Id").GetValue(maps.Last()), Property(designer, "ActiveMappingNodeId").GetValue(designer));
            Assert.Contains("add a deliver node before saving", rendered.ToHtmlString());
            Invoke(designer, "OpenMappingDesigner", maps.First());
            await designer.SetParametersAsync(ParameterView.Empty);
            Assert.Equal(Property(maps.First(), "Id").GetValue(maps.First()), Property(designer, "ActiveMappingNodeId").GetValue(designer));
            Assert.Equal(2, components.Created.OfType<XmlMappingEditor>().Count());
            Assert.Same(preview, Field(editor, "_preview").GetValue(editor));
        });
    }

    [Theory]
    [InlineData("{\"version\":9,\"fields\":[]}")]
    [InlineData("{\"version\":1,\"futureProperty\":42}")]
    [InlineData("{\"version\":1,\"fields\":[null]}")]
    [InlineData("{\"version\":2,\"fields\":[{\"operations\":null}]}")]
    [InlineData("{\"version\":2,\"fields\":[{\"operations\":[null]}]}")]
    [InlineData("{\"version\":2,\"fields\":[{\"operations\":[{\"kind\":\"Unknown\"}]}]}")]
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
    public async Task PipelineScaffolds_AppearInTheImportPanelWithDownloadControlsAndWarnings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-pipeline-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "Send.btp"), "<Document><Stages><Stage CategoryId='9d0e4107-4cce-4536-83fa-4a5040674ad6'><Components/></Stage></Stages></Document>");
            var project = Path.Combine(directory, "Pipelines.btproj");
            File.WriteAllText(project, "<Project><ItemGroup><Pipeline Include='Send.btp'/></ItemGroup></Project>");
            var analysis = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project }, default);
            var view = JsonSerializer.Deserialize<BizTalkSolutionAnalysisViewModel>(JsonSerializer.Serialize(analysis, Options), Options)!;
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
                Field(designer, "_bizTalkAnalysis").SetValue(designer, view);
                Field(designer, "_showBizTalkPanel").SetValue(designer, true);
                await designer.SetParametersAsync(ParameterView.Empty);
                var html = rendered.ToHtmlString();
                Assert.Contains(Assert.Single(view.FlowCandidates).ScaffoldedModules.Single().FileName, html);
                Assert.Contains("Download all (.zip)", html);
                Assert.Contains(">Download</button>", html);
                Assert.Contains("C# stubs fail until implemented", html);
                Assert.Contains("does not approve execution", html);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task OperationControls_UpgradeVersionAndRejectInvalidMovesKindsAndExcessOperations()
    {
        var editor = new XmlMappingEditor();
        Property(editor, "Value").SetValue(editor, XmlMappingTests.Json());
        Invoke(editor, "OnParametersSet");
        var map = (XmlMappingViewModel)Field(editor, "_map").GetValue(editor)!;
        var field = map.Fields[0];
        await (Task)Invoke(editor, "AddOperationAsync", field)!;
        Assert.Equal(2, map.Version);
        var operation = Assert.Single(field.Operations);
        await (Task)Invoke(editor, "SetOperationKindAsync", operation, "Prefix")!;
        Assert.Equal("", operation.Argument);
        operation.Argument = "literal";
        await (Task)Invoke(editor, "SetOperationKindAsync", operation, "UppercaseAscii")!;
        Assert.Null(operation.Argument);
        var original = editor.Value;
        await (Task)Invoke(editor, "MoveOperationAsync", field, operation, -1)!;
        Assert.Equal(original, editor.Value);
        Assert.Contains("cannot be moved", Assert.IsType<string>(Field(editor, "_error").GetValue(editor)));
        await (Task)Invoke(editor, "SetOperationKindAsync", operation, "Unknown")!;
        Assert.Equal(original, editor.Value);
        Assert.Contains("supported string operation", Assert.IsType<string>(Field(editor, "_error").GetValue(editor)));
        for (var index = 1; index < 16; index++) await (Task)Invoke(editor, "AddOperationAsync", field)!;
        original = editor.Value;
        await (Task)Invoke(editor, "AddOperationAsync", field)!;
        Assert.Equal(16, field.Operations.Count);
        Assert.Equal(original, editor.Value);
        Assert.Contains("at most 16", Assert.IsType<string>(Field(editor, "_error").GetValue(editor)));
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
        var field = ((XmlMappingViewModel)Field(editor, "_map").GetValue(editor)!).Fields[0];
        await (Task)Invoke(editor, "AddOperationAsync", field)!;
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

    private sealed class TrackingActivator : IComponentActivator
    {
        public List<IComponent> Created { get; } = [];
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            Created.Add(component);
            return component;
        }
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
