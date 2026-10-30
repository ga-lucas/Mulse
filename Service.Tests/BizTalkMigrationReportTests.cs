using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mulse.Modules.Pipeline;
using Mulse.Modules.Security;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkMigrationReportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-migration-report-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private const string DirectMap = """
        <mapsource>
          <SrcTree><Reference Location="Source" /></SrcTree>
          <TrgTree RootNode_Name="Target"><Reference Location="Target" /></TrgTree>
          <Links><Link LinkFrom="/*[local-name()='&lt;Schema&gt;']/*[local-name()='Id']"
                       LinkTo="/*[local-name()='&lt;Schema&gt;']/*[local-name()='Id']" /></Links>
        </mapsource>
        """;

    private const string InventoryOdx = """
        // #if __DESIGNER_DATA
        <?xml version="1.0"?>
        <om:MetaModel xmlns:om="http://schemas.microsoft.com/BizTalk/2003/DesignerData">
          <om:Element Type="Send">
            <om:Property Name="Name" Value="&lt;Unsafe&gt;" />
            <om:Property Name="PortName" Value="OutputPort" />
          </om:Element>
        </om:MetaModel>
        // #endif
        """;

    [Fact]
    public async Task DirectMap_IsReviewRequiredAndExplicitlyNotWiredOrVerified()
    {
        var candidate = await AnalyzeProject(("Map", "Direct.btm", DirectMap));
        var report = Assert.IsType<FlowMigrationReport>(candidate.DraftFlow.Migration);
        var artifact = Assert.Single(report.Artifacts);
        Assert.Equal(MigrationTranslationStatus.ReviewRequired, artifact.Status);
        Assert.Equal(Path.Combine(_directory, "Direct.btm"), artifact.SourcePath);
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Code == "BT-MAP-NOT-WIRED" && diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "BT-SEMANTICS-UNVERIFIED");
        Assert.DoesNotContain(report.Artifacts, artifact => artifact.Status == MigrationTranslationStatus.FullyTranslated);
        Assert.Equal(1, report.Version);
        Assert.Equal("BizTalk", report.SourceSystem);
        Assert.Equal(candidate.SourceProjectPath, report.SourceProjectPath);
        Assert.False(candidate.DraftFlow.Enabled);
    }

    [Theory]
    [InlineData("<mapsource><Links><Link LinkFrom='1' LinkTo='2' /></Links><Functoid Functoid-FID='260' /></mapsource>", "BT-MAP-UNTRANSLATED")]
    [InlineData("<mapsource>", "BT-MAP-NOT-ANALYZED")]
    [InlineData("<Unknown />", "BT-MAP-NOT-ANALYZED")]
    [InlineData(null, "BT-ARTIFACT-MISSING")]
    public async Task UnsupportedOrMissingMaps_ProduceBlockingDiagnosticsRatherThanVanishing(string? source, string code)
    {
        var report = (await AnalyzeProject(("Map", "Map.btm", source))).DraftFlow.Migration!;
        var artifact = Assert.Single(report.Artifacts);
        Assert.Equal(MigrationTranslationStatus.Unsupported, artifact.Status);
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Code == code && diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
        Assert.All(artifact.Diagnostics, diagnostic =>
        {
            Assert.False(string.IsNullOrWhiteSpace(diagnostic.Remediation));
            Assert.Equal(artifact.SourcePath, diagnostic.SourcePath);
        });
    }

    [Fact]
    public async Task MixedDirectAndFunctoidMap_DoesNotClaimPartialXsltIsComplete()
    {
        var source = DirectMap.Replace("</mapsource>", "<Links><Link LinkFrom='1' LinkTo='2' /></Links><Functoid Functoid-FID='260' /></mapsource>");
        var report = (await AnalyzeProject(("Map", "Mixed.btm", source))).DraftFlow.Migration!;
        var artifact = Assert.Single(report.Artifacts);
        Assert.Equal(MigrationTranslationStatus.Unsupported, artifact.Status);
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Code == "BT-MAP-UNTRANSLATED");
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Code == "BT-MAP-NOT-WIRED");
    }

    [Fact]
    public async Task SchemaAndPipeline_AreConservativelyClassifiedWithSourceProvenance()
    {
        var report = (await AnalyzeProject(("Schema", "Message.xsd", "<schema />"),
            ("Pipeline", "Receive.btp", "<Document />"))).DraftFlow.Migration!;
        Assert.Equal(MigrationTranslationStatus.ReviewRequired, report.Artifacts.Single(artifact => artifact.Kind == "Schema").Status);
        Assert.Equal(MigrationTranslationStatus.Unsupported, report.Artifacts.Single(artifact => artifact.Kind == "Pipeline").Status);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "BT-BINDING-MISSING");
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "BT-CONFIGURATION-REQUIRED");
        Assert.All(report.Diagnostics, diagnostic => Assert.False(string.IsNullOrWhiteSpace(diagnostic.SourcePath)));
    }

    [Fact]
    public async Task ReferencedMap_UsesOwningProjectsAnalysisInsteadOfReportingItUnreadable()
    {
        await AnalyzeProject(("XLang", "Process.odx", "<Root />"));
        var library = Path.Combine(_directory, "Library");
        Directory.CreateDirectory(library);
        File.WriteAllText(Path.Combine(library, "Shared.btm"), DirectMap);
        new XDocument(new XElement("Project", new XElement("ItemGroup", new XElement("Map", new XAttribute("Include", "Shared.btm")))))
            .Save(Path.Combine(library, "Library.btproj"));
        var projectPath = Path.Combine(_directory, "Import.btproj");
        var document = XDocument.Load(projectPath);
        document.Root!.Add(new XElement("ItemGroup", new XElement("ProjectReference", new XAttribute("Include", @"Library\Library.btproj"))));
        document.Save(projectPath);
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = _directory }, CancellationToken.None);
        var report = result.FlowCandidates.Single(candidate => candidate.SourceProjectPath == projectPath).DraftFlow.Migration!;
        var map = Assert.Single(report.Artifacts, artifact => artifact.Kind == "Map");
        Assert.Equal(MigrationTranslationStatus.ReviewRequired, map.Status);
        Assert.Contains(map.Diagnostics, diagnostic => diagnostic.Code == "BT-MAP-NOT-WIRED");
        Assert.DoesNotContain(map.Diagnostics, diagnostic => diagnostic.Code == "BT-MAP-NOT-ANALYZED");
        var indexedMap = Assert.Single(report.MapInventory!.Candidates);
        Assert.Equal("Shared", indexedMap.Name);
        Assert.Equal(Path.Combine(library, "Library.btproj"), indexedMap.SourceProjectPath);
        Assert.Equal(Path.Combine(library, "Shared.btm"), indexedMap.SourcePath);
        Assert.Equal(MigrationTranslationStatus.ReviewRequired, indexedMap.Status);
        Assert.Contains("not wired or verified", indexedMap.Limitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SeparateUnreferencedMapProject_IsListedAsAWorkspaceCandidateNotAnOrchestrationArtifact()
    {
        await AnalyzeProject(("XLang", "Process.odx", InventoryOdx));
        var mapDirectory = Path.Combine(_directory, "SeparateMaps");
        Directory.CreateDirectory(mapDirectory);
        File.WriteAllText(Path.Combine(mapDirectory, "Shared.btm"), DirectMap);
        new XDocument(new XElement("Project", new XElement("ItemGroup", new XElement("Map", new XAttribute("Include", "Shared.btm")))))
            .Save(Path.Combine(mapDirectory, "Maps.btproj"));

        var result = await new BizTalkImportService().AnalyzeAsync(
            new AnalyzeBizTalkSolutionRequest { SourcePath = _directory }, CancellationToken.None);
        var importProjectPath = Path.Combine(_directory, "Import.btproj");
        var report = result.FlowCandidates.Single(flow => flow.SourceProjectPath == importProjectPath).DraftFlow.Migration!;
        var candidate = Assert.Single(report.MapInventory!.Candidates);
        Assert.Equal(Path.Combine(mapDirectory, "Maps.btproj"), candidate.SourceProjectPath);
        Assert.Equal(Path.Combine(mapDirectory, "Shared.btm"), candidate.SourcePath);
        Assert.DoesNotContain(report.Artifacts, artifact => artifact.SourcePath == candidate.SourcePath);
        Assert.Equal(MigrationTranslationStatus.ReviewRequired, candidate.Status);
        Assert.Contains("not wired or verified", candidate.Limitation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CustomCodeAndUnresolvedReferences_RemainExplicitBlockingWork()
    {
        await AnalyzeProject(("XLang", "Process.odx", "<Root />"));
        var customProject = Path.Combine(_directory, "Custom.csproj");
        File.WriteAllText(customProject, "<Project><PropertyGroup><AssemblyName>Custom</AssemblyName></PropertyGroup></Project>");
        var projectPath = Path.Combine(_directory, "Import.btproj");
        var document = XDocument.Load(projectPath);
        document.Root!.Add(new XElement("ItemGroup",
            new XElement("ProjectReference", new XAttribute("Include", "Custom.csproj")),
            new XElement("ProjectReference", new XAttribute("Include", "Missing.btproj"))));
        document.Save(projectPath);
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = _directory }, CancellationToken.None);
        var report = result.FlowCandidates.Single().DraftFlow.Migration!;
        var custom = report.Artifacts.Single(artifact => artifact.Kind == "CustomAssembly");
        Assert.Equal(customProject, custom.SourcePath);
        Assert.Equal(MigrationTranslationStatus.Unsupported, custom.Status);
        Assert.Contains(custom.Diagnostics, diagnostic => diagnostic.Code == "BT-CUSTOM-CODE" && diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
        Assert.Contains(report.Diagnostics, diagnostic => diagnostic.Code == "BT-DEPENDENCY-UNRESOLVED"
            && diagnostic.Message.Contains("Missing.btproj", StringComparison.Ordinal) && diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
    }

    [Fact]
    public async Task SplitOrchestrations_ReportOnlyTheirOwnStubAndKeepSharedArtifacts()
    {
        const string loop = """
            // #if __DESIGNER_DATA
            <?xml version="1.0" encoding="utf-16"?>
            <om:MetaModel xmlns:om="http://schemas.microsoft.com/BizTalk/2003/DesignerData">
              <om:Element Type="ServiceDeclaration"><om:Element Type="Loop"><om:Property Name="Name" Value="Loop" /></om:Element></om:Element>
            </om:MetaModel>
            // #endif
            """;
        await AnalyzeProject(("XLang", "Complex.odx", loop), ("XLang", "Simple.odx", "<Root />"), ("Map", "Shared.btm", DirectMap));
        var result = await Analyze();
        Assert.Equal(2, result.FlowCandidates.Count);
        var complex = result.FlowCandidates.Single(candidate => candidate.DraftFlow.Migration!.SourceOrchestration == "Complex").DraftFlow.Migration!;
        var simple = result.FlowCandidates.Single(candidate => candidate.DraftFlow.Migration!.SourceOrchestration == "Simple").DraftFlow.Migration!;
        Assert.Equal(MigrationTranslationStatus.StubGenerated, complex.Artifacts.Single(artifact => artifact.Kind == "Orchestration").Status);
        Assert.Equal(MigrationTranslationStatus.ReviewRequired, simple.Artifacts.Single(artifact => artifact.Kind == "Orchestration").Status);
        Assert.DoesNotContain(simple.Artifacts, artifact => artifact.SourcePath.EndsWith("Complex.odx", StringComparison.Ordinal));
        Assert.All(new[] { simple, complex }, report => Assert.Contains(report.Artifacts, artifact => artifact.Kind == "Map"));
    }

    [Fact]
    public async Task Report_PersistsAcrossDiskReloadApiRoundTripAndLegacyUpdateAndDuplicate()
    {
        var draft = (await AnalyzeProject(("Map", "Map.btm", DirectMap), ("XLang", "Process.odx", InventoryOdx))).DraftFlow;
        Assert.NotNull(draft.Migration!.Artifacts.Single(artifact => artifact.Kind == "Orchestration").Orchestration);
        Assert.Equal(1, draft.Migration.MapInventory!.TotalDiscovered);
        Assert.Equal(MigrationTranslationStatus.ReviewRequired, Assert.Single(draft.Migration.MapInventory.Candidates).Status);
        var imported = FlowMappings.MapPipeline(draft);
        var store = Store();
        await store.UpsertFlowAsync(imported, CancellationToken.None);
        var reloaded = Store().GetState().Pipelines.Single();
        AssertJsonEqual(draft.Migration, reloaded.Migration);
        var responseJson = JsonSerializer.Serialize(FlowMappings.MapFlow(reloaded), JsonOptions);
        var create = JsonSerializer.Deserialize<CreateFlowRequest>(responseJson, JsonOptions)!;
        var update = JsonSerializer.Deserialize<UpdateFlowRequest>(responseJson, JsonOptions)!;
        AssertJsonEqual(draft.Migration, FlowMappings.MapPipeline(create).Migration);
        AssertJsonEqual(draft.Migration, FlowMappings.MapPipeline(update).Migration);
        var legacy = JsonSerializer.Deserialize<UpdateFlowRequest>(responseJson, JsonOptions)! with { Migration = null };
        var catalog = FakeModuleCatalog.WithModules(
            (imported.Fetches[0].Fetch.Module, ModuleKind.Fetch), (imported.Parses[0].Parse.Module, ModuleKind.Parse),
            (imported.Deliveries[0].Render.Module, ModuleKind.Render), (imported.Deliveries[0].Delivers[0].Deliver.Module, ModuleKind.Deliver));
        var service = new FlowDefinitionService(Store(), catalog);
        var updated = await service.UpdateAsync(imported.Id, FlowMappings.MapPipeline(legacy), CancellationToken.None);
        AssertJsonEqual(draft.Migration, updated.Migration);
        var disabled = FlowMappings.WithEnabled(updated, false);
        AssertJsonEqual(draft.Migration, disabled.Migration);
        var categorized = await service.UpdateAsync(imported.Id, FlowMappings.CopyPipeline(disabled, disabled.Id, false, "Migrated"), CancellationToken.None);
        AssertJsonEqual(draft.Migration, categorized.Migration);
        Assert.Equal("Migrated", categorized.Category);
        var copy = await service.DuplicateAsync(imported.Id, "copy", CancellationToken.None);
        AssertJsonEqual(draft.Migration, copy.Migration);
        Assert.False(copy.Enabled);
        AssertJsonEqual(draft.Migration, Store().GetState().Pipelines.Single(flow => flow.Id == "copy").Migration);
    }

    [Fact]
    public async Task DesignedFlowAndConfigurationResolution_PreserveSnapshotWithoutResolvingReportText()
    {
        var report = (await AnalyzeProject(("Map", "Map.btm", DirectMap))).DraftFlow.Migration!;
        report = report with { Warnings = [.. report.Warnings, "{{secret:runtime-only}}"] };
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState { ConfigValues = new() { ["runtime-only"] = "test-runtime-value" } });
        var catalog = FakeModuleCatalog.WithModules(("fetch", ModuleKind.Fetch), ("parse", ModuleKind.Parse),
            ("render", ModuleKind.Render), ("deliver", ModuleKind.Deliver));
        var service = new FlowDefinitionService(store, catalog);
        var designer = new FlowDesignService(catalog, service);
        var pipeline = await designer.CreateFlowAsync(new CreateDesignedFlowRequest
        {
            Id = "designed", Enabled = false, Migration = report,
            Trigger = new FlowTriggerRequest { Mode = PipelineTriggerMode.OnDemand },
            Fetches = [new() { Id = "in", Fetch = new() { Module = "fetch" } }],
            Parses = [new() { Id = "parsed", FetchNodeIds = ["in"], Parse = new() { Module = "parse" } }],
            Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }]
        }, CancellationToken.None);
        AssertJsonEqual(report, pipeline.Migration);
        pipeline.Fetches[0].Fetch.Settings["path"] = "{{secret:runtime-only}}";
        var resolved = new ConfigValueResolver(store).Resolve(pipeline);
        AssertJsonEqual(report, resolved.Migration);
        Assert.Equal("test-runtime-value", resolved.Fetches[0].Fetch.Settings["path"]);
        Assert.DoesNotContain("test-runtime-value", JsonSerializer.Serialize(resolved.Migration, JsonOptions));
    }

    [Fact]
    public async Task Ui_CreateAndUpdateAndReopen_RetainReportAndWarnThatItIsNotApproval()
    {
        var candidate = await AnalyzeProject(("Map", "Direct.btm", DirectMap), ("XLang", "Process.odx", InventoryOdx));
        var view = JsonSerializer.Deserialize<BizTalkFlowCandidateViewModel>(JsonSerializer.Serialize(candidate, JsonOptions), JsonOptions)!;
        var handler = new SavingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var designer = new Designer();
        Property("ApiClient").SetValue(designer, new MulseApiClient(http));
        Property("Navigation").SetValue(designer, new TestNavigation());
        Invoke(designer, "ApplyBizTalkCandidate", view);
        Field("_enabled").SetValue(designer, true);
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Contains("verified translation support", Assert.IsType<string>(Field("_errorMessage").GetValue(designer)));
        Assert.Equal(0, handler.Saves);
        Field("_enabled").SetValue(designer, false);
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field("_errorMessage").GetValue(designer));
        Assert.NotNull(handler.Saved);
        AssertJsonEqual(candidate.DraftFlow.Migration, handler.Saved!.Migration);
        Assert.StartsWith("Created flow '", Assert.IsType<string>(Field("_statusMessage").GetValue(designer)));
        var reopened = JsonSerializer.Deserialize<FlowViewModel>(JsonSerializer.Serialize(FlowMappings.MapFlow(handler.Saved), JsonOptions), JsonOptions)!;
        Invoke(designer, "BuildFromFlow", reopened);
        AssertJsonEqual(reopened.Migration, Field("_migration").GetValue(designer));
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field("_errorMessage").GetValue(designer));
        Assert.Equal(2, handler.Saves);
        AssertJsonEqual(candidate.DraftFlow.Migration, handler.Saved.Migration);
        Invoke(designer, "ClearCanvas");
        AssertJsonEqual(reopened.Migration, Field("_migration").GetValue(designer));

        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<MigrationReport>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(MigrationReport.Report)] = reopened.Migration }));
            return rendered.ToHtmlString();
        });
        Assert.Contains("Migration report - BizTalk", html);
        Assert.Contains("blocking diagnostic(s)", html);
        Assert.Contains("Import-time snapshot, not execution approval", html);
        Assert.Contains("execution blocked", html);
        Assert.Contains("verified translation support", html);
        Assert.Contains("BT-MAP-NOT-WIRED", html);
        Assert.Contains("Next action:", html);
        Assert.Contains(candidate.SourceProjectPath, html);
        Assert.Contains("Orchestration source inventory", html);
        Assert.Contains("not an executable sequence", html);
        Assert.Contains("Source line", html);
        Assert.Contains("OutputPort", html);
        Assert.Contains("Maps found in analyzed source", html);
        Assert.Contains("map use by this orchestration is unverified", html);
        Assert.Contains("Direct.btm", html);
        Assert.Contains("&lt;Unsafe&gt;", html);
        Assert.DoesNotContain("<Unsafe>", html);
    }

    [Fact]
    public void OldFlowsWithoutMetadata_StillDeserializeWithNullReport()
    {
        var pipeline = JsonSerializer.Deserialize<PipelineDefinition>("{\"id\":\"manual\"}", JsonOptions)!;
        Assert.Null(pipeline.Migration);
        Assert.Null(FlowMappings.MapFlow(pipeline).Migration);
    }

    private async Task<BizTalkFlowCandidateResponse> AnalyzeProject(params (string Kind, string Name, string? Content)[] artifacts)
    {
        Directory.CreateDirectory(_directory);
        foreach (var artifact in artifacts.Where(artifact => artifact.Content is not null))
            File.WriteAllText(Path.Combine(_directory, artifact.Name), artifact.Content);
        new XDocument(new XElement("Project", new XElement("PropertyGroup", new XElement("AssemblyName", "Import")),
            new XElement("ItemGroup", artifacts.Select(artifact => new XElement(artifact.Kind, new XAttribute("Include", artifact.Name))))))
            .Save(Path.Combine(_directory, "Import.btproj"));
        return (await Analyze()).FlowCandidates.First();
    }

    private Task<BizTalkSolutionAnalysisResponse> Analyze()
        => new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = Path.Combine(_directory, "Import.btproj") }, CancellationToken.None);

    private RuntimeConfigurationStore Store() => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }),
        new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);

    private static void AssertJsonEqual(object? expected, object? actual)
        => Assert.Equal(JsonSerializer.Serialize(expected, JsonOptions), JsonSerializer.Serialize(actual, JsonOptions));

    private static FieldInfo Field(string name) => typeof(Designer).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static PropertyInfo Property(string name) => typeof(Designer).GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static object? Invoke(Designer designer, string method, params object?[] args)
        => typeof(Designer).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(designer, args);

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/designer");
        protected override void NavigateToCore(string uri, bool forceLoad) => Uri = ToAbsoluteUri(uri).ToString();
    }

    private sealed class SavingHandler : HttpMessageHandler
    {
        public PipelineDefinition? Saved { get; private set; }
        public int Saves { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            object payload;
            if (request.RequestUri!.AbsolutePath == "/api/config-values/usages") payload = Array.Empty<ConfigReferenceUsageViewModel>();
            else if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put)
            {
                var json = await request.Content!.ReadAsStringAsync(cancellationToken);
                Saved = request.Method == HttpMethod.Post
                    ? FlowMappings.MapPipeline(JsonSerializer.Deserialize<CreateFlowRequest>(json, JsonOptions)!)
                    : FlowMappings.MapPipeline(JsonSerializer.Deserialize<UpdateFlowRequest>(json, JsonOptions)!);
                Saves++;
                payload = FlowMappings.MapFlow(Saved);
            }
            else if (Saved is not null && request.RequestUri.AbsolutePath.StartsWith("/api/flows/", StringComparison.Ordinal)) payload = FlowMappings.MapFlow(Saved);
            else throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, payload.GetType(), JsonOptions), System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
