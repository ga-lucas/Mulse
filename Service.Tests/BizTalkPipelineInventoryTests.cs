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
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkPipelineInventoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-pipeline-evidence-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private const string Assemble = "9d0e4107-4cce-4536-83fa-4a5040674ad6";
    private const string Decode = "9d0e4103-4cce-4536-83fa-4a5040674ad6";
    private const string Secret = "DO_NOT_PERSIST_PROPERTY_VALUE";

    [Fact]
    public async Task SourceOrderAndIdentities_PersistWithoutPropertyValuesAndRenderSafely()
    {
        var document = new XElement("Document", new XAttribute("PolicyFilePath", Secret), new XElement("Description", Secret),
            new XElement("Stages", Stage(Assemble, Component("Example.<Unsafe>", "Password"), Component("Example.Second", "Retry")),
                Stage("9d0e4108-4cce-4536-83fa-4a5040674ad6")));
        var report = await Analyze(document.ToString());
        var artifact = Assert.Single(report.Artifacts);
        Assert.Equal(MigrationTranslationStatus.Unsupported, artifact.Status);
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-UNTRANSLATED" && issue.Severity == MigrationDiagnosticSeverity.Blocking);
        var inventory = artifact.Pipeline!;
        Assert.Equal("Send", inventory.Direction);
        Assert.Equal(2, inventory.TotalStages);
        Assert.Equal(2, inventory.TotalComponents);
        Assert.Equal(new[] { "Assemble", "Encode" }, inventory.Stages.Select(stage => stage.StageName));
        Assert.Equal(new[] { "Example.<Unsafe>", "Example.Second" }, inventory.Stages[0].Components.Select(component => component.ClassName));
        Assert.Equal(new[] { 1, 2 }, inventory.Stages[0].Components.Select(component => component.Order));
        Assert.True(inventory.Stages[0].Components[0].SourceLine > inventory.Stages[0].SourceLine);
        Assert.Equal("Password", Assert.Single(inventory.Stages[0].Components[0].PropertyNames));
        var json = JsonSerializer.Serialize(report, Options);
        Assert.DoesNotContain(Secret, json);
        var restored = JsonSerializer.Deserialize<FlowMigrationReport>(json, Options)!;
        Assert.Equal(json, JsonSerializer.Serialize(restored, Options));
        var pipeline = FlowMappings.MapPipeline(new BizTalkDraftFlowResponse("pipeline-draft", false, new(PipelineTriggerMode.OnDemand, null, false), [], [], [], [], [], [], report));
        var storePath = Path.Combine(_directory, "state.json");
        RuntimeConfigurationStore Store() => new(Microsoft.Extensions.Options.Options.Create(new MulseOptions { RuntimeStatePath = storePath }),
            new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
        await Store().UpsertFlowAsync(pipeline, default);
        Assert.Equal(json, JsonSerializer.Serialize(Store().GetState().Pipelines.Single().Migration, Options));
        var view = JsonSerializer.Deserialize<FlowMigrationReportViewModel>(json, Options)!;
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MigrationReport>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(MigrationReport.Report)] = view }))).ToHtmlString());
        Assert.Contains("Pipeline source inventory", html);
        Assert.Contains("Example.&lt;Unsafe&gt;", html);
        Assert.Contains("Property names only", html);
        Assert.Contains("execution blocked", html);
        Assert.DoesNotContain(Secret, html);
    }

    [Theory]
    [InlineData("<Document/>")]
    [InlineData("<Document><Stages/></Document>")]
    [InlineData("<Document><Stages/><Stages/></Document>")]
    [InlineData("<Unrecognized/>")]
    [InlineData("<Document>")]
    [InlineData("<!DOCTYPE Document SYSTEM 'file:///not-read'><Document/>")]
    public async Task UnreadableOrUnknownShape_LeavesBlockingEvidenceNotAnEmptySuccess(string xml)
    {
        var artifact = Assert.Single((await Analyze(xml)).Artifacts);
        Assert.Null(artifact.Pipeline);
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-NOT-ANALYZED");
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-UNTRANSLATED");
    }

    [Theory]
    [InlineData(Decode, "Receive")]
    [InlineData(Assemble, "Send")]
    [InlineData("unknown", "Unknown")]
    public async Task Direction_ComesFromStageCategoryOnly(string category, string expected)
    {
        var document = new XElement("Document", new XElement("Description", Decode), new XElement("Stages", Stage(category, Component("Example", "Value"))));
        var artifact = Assert.Single((await Analyze(document.ToString())).Artifacts);
        Assert.Equal(expected, artifact.Pipeline!.Direction);
        if (expected == "Unknown") Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-DIRECTION");
    }

    [Fact]
    public async Task MixedCategoriesAndMisplacedComponents_AreNotACompleteDirectionAssertion()
    {
        var document = new XElement("Document", new XElement("Component", new XElement("Name", "OutsideStage")),
            new XElement("Stages", Stage(Decode), Stage(Assemble)));
        var artifact = Assert.Single((await Analyze(document.ToString())).Artifacts);
        Assert.Equal("Unknown", artifact.Pipeline!.Direction);
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-STRUCTURE");
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-DIRECTION");
    }

    [Fact]
    public async Task Bounds_RetainTotalAndOmittedCounts()
    {
        var components = Enumerable.Range(0, 257).Select(index => Component("Example" + index,
            index == 0 ? Enumerable.Range(0, 65).Select(number => "P" + number).ToArray() : ["Property"]));
        var document = new XElement("Document", new XElement("Stages", Stage(Assemble, components.ToArray()), Enumerable.Range(0, 64).Select(_ => Stage(Assemble))));
        var artifact = Assert.Single((await Analyze(document.ToString())).Artifacts);
        var inventory = Assert.IsType<PipelineMigrationInventory>(artifact.Pipeline);
        Assert.Equal(65, inventory.TotalStages);
        Assert.Equal(64, inventory.Stages.Count);
        Assert.Equal(1, inventory.OmittedStageCount);
        Assert.Equal(257, inventory.TotalComponents);
        Assert.Equal(1, inventory.OmittedComponentCount);
        var component = inventory.Stages[0].Components[0];
        Assert.Equal(65, component.TotalProperties);
        Assert.Equal(64, component.PropertyNames.Count);
        Assert.Equal(1, component.OmittedPropertyCount);
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-INVENTORY-LIMIT");
    }

    [Fact]
    public async Task AmbiguousComponentAndOversizedMetadata_RequireReview()
    {
        var component = Component(new string('x', 513), "Password");
        component.Add(new XElement("Name", "Duplicate"));
        var artifact = Assert.Single((await Analyze(new XElement("Document", new XElement("Stages", Stage(Assemble, component))).ToString())).Artifacts);
        Assert.Equal(512, artifact.Pipeline!.Stages[0].Components[0].ClassName.Length);
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-COMPONENT-METADATA");
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-METADATA-LIMIT");
    }

    [Fact]
    public async Task ComplexIdentityMetadata_IsNotFlattenedIntoTheSavedReport()
    {
        var component = Component("Example", "Password");
        component.Element("Name")!.ReplaceWith(new XElement("Name", new XElement("Value", Secret)));
        component.Element("Version")!.ReplaceWith(new XElement("Version", new XElement("Value", Secret)));
        var report = await Analyze(new XElement("Document", new XElement("Stages", Stage(Assemble, component))).ToString());
        var artifact = Assert.Single(report.Artifacts);
        Assert.Contains(artifact.Diagnostics, issue => issue.Code == "BT-PIPELINE-COMPONENT-METADATA");
        Assert.Equal("", artifact.Pipeline!.Stages[0].Components[0].ClassName);
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(report, Options));
    }

    private static XElement Stage(string category, params XElement[] components) => new("Stage", new XAttribute("CategoryId", category), new XElement("Components", components));
    private static XElement Component(string name, params string[] properties) => new("Component", new XElement("Name", name), new XElement("Version", "1.0"),
        new XElement("Description", Secret), new XElement("CachedDisplayName", Secret), new XElement("Properties", properties.Select(property =>
            new XElement("Property", new XAttribute("Name", property), new XElement("Value", Secret)))));

    private async Task<FlowMigrationReport> Analyze(string xml)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Send.btp"), xml);
        var path = Path.Combine(_directory, "Pipelines.btproj");
        File.WriteAllText(path, "<Project><ItemGroup><Pipeline Include='Send.btp'/></ItemGroup></Project>");
        var analysis = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = path }, default);
        var candidate = analysis.FlowCandidates.Single();
        var stub = Assert.Single(candidate.ScaffoldedModules);
        Assert.EndsWith(".cs", stub.FileName);
        Assert.Equal("Send", stub.SourceArtifactName);
        Assert.Contains("throw new NotSupportedException", stub.SourceCode);
        Assert.DoesNotContain(Secret, stub.SourceCode);
        var report = candidate.DraftFlow.Migration!;
        var direction = report.Artifacts.Single().Pipeline?.Direction;
        Assert.Equal(direction == "Receive" ? 1 : 0, analysis.BizTalkProjects.Single().ReceivePipelineCount);
        Assert.Equal(direction == "Send" ? 1 : 0, analysis.BizTalkProjects.Single().SendPipelineCount);
        return report;
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
