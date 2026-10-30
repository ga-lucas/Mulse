using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Pipeline;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkMapScriptInventoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-map-scripts-" + Guid.NewGuid().ToString("N"));
    private const string Code = "public string Convert(IBaseMessage message) { return \"DO_NOT_PERSIST_SCRIPT_BODY\"; }";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task ScriptWithoutFid_RemainsUnsupportedWithSourceLinesHashesAndNoPersistedBody()
    {
        var root = XDocument.Parse(NativeMapImportTests.Map()).Root!;
        root.Add(new XElement("Functoid", new XAttribute("FunctoidID", "17"), new XElement("Scripts", new XElement("Script", new XAttribute("Language", "CSharp"), new XCData(Code)))));
        var report = await Analyze(root.ToString());
        var map = Assert.Single(report.MapInventory!.Candidates);
        Assert.Equal(MigrationTranslationStatus.Unsupported, map.Status);
        var script = Assert.Single(map.ScriptInventory!.Scripts);
        Assert.Equal(1, map.ScriptInventory.TotalScripts);
        Assert.Equal("CSharp", script.Language);
        Assert.Equal("17", script.FunctoidId);
        Assert.True(script.SourceLine > 0);
        Assert.Equal(Code.Length, script.CharacterCount);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Code))).ToLowerInvariant(), script.Sha256);
        Assert.Equal(new[] { "IBaseMessage" }, script.BizTalkApiReferences);
        Assert.Contains(report.Artifacts.Single().Diagnostics, diagnostic => diagnostic.Code == "BT-MAP-SCRIPTS-UNTRANSLATED" && diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
        var serialized = JsonSerializer.Serialize(report, Options);
        Assert.DoesNotContain("DO_NOT_PERSIST_SCRIPT_BODY", serialized);
        var view = JsonSerializer.Deserialize<FlowMigrationReportViewModel>(serialized, Options)!;
        Assert.Equal(script.Sha256, view.MapInventory!.Candidates.Single().ScriptInventory!.Scripts.Single().Sha256);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MigrationReport>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(MigrationReport.Report)] = view }))).ToHtmlString());
        Assert.Contains("Script evidence", html);
        Assert.Contains("IBaseMessage", html);
        Assert.Contains("do not prove portability", html);
        Assert.DoesNotContain("DO_NOT_PERSIST_SCRIPT_BODY", html);
        var pipeline = FlowMappings.MapPipeline(new BizTalkDraftFlowResponse("script-draft", false, new(PipelineTriggerMode.OnDemand, null, false), [], [], [], [], [], [], report));
        var store = new RuntimeConfigurationStore(Microsoft.Extensions.Options.Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }),
            new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
        await store.UpsertFlowAsync(pipeline, default);
        var restored = new RuntimeConfigurationStore(Microsoft.Extensions.Options.Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }),
            new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance).GetState().Pipelines.Single().Migration;
        Assert.Equal(serialized, JsonSerializer.Serialize(restored, Options));
    }

    [Fact]
    public async Task ScriptOnlyMap_IsInventoriedWithBoundedDetailsEvenWithoutScaffold()
    {
        var root = new XElement("mapsource", Enumerable.Range(0, 130).Select(index => new XElement("Script", new XAttribute("Language", "Unknown"), new XCData("not executed " + index))));
        var report = await Analyze(root.ToString());
        var scripts = Assert.Single(report.MapInventory!.Candidates).ScriptInventory!;
        Assert.Equal(130, scripts.TotalScripts);
        Assert.Equal(128, scripts.Scripts.Count);
        Assert.Equal(2, scripts.OmittedCount);
        Assert.All(scripts.Scripts, script => Assert.Empty(script.BizTalkApiReferences));
        Assert.Contains(report.Artifacts.Single().Diagnostics, diagnostic => diagnostic.Code == "BT-MAP-SCRIPTS-UNTRANSLATED");
    }

    private async Task<FlowMigrationReport> Analyze(string xml)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Map.btm"), xml);
        var path = Path.Combine(_directory, "Maps.btproj");
        File.WriteAllText(path, "<Project><ItemGroup><Map Include='Map.btm'/></ItemGroup></Project>");
        return (await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = path }, default)).FlowCandidates.Single().DraftFlow.Migration!;
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
