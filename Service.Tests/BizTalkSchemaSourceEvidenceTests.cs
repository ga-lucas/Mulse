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

public sealed class BizTalkSchemaSourceEvidenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-schema-evidence-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData(false, "Contracts.Sub.Invoice", "LiteralMetadataWithNamingConventions")]
    [InlineData(true, "Explicit.Messages.Renamed", "ExplicitNamespaceAndTypeName")]
    public async Task LiteralClassEvidence_RetainsExactProjectPathAndMetadata(bool explicitMetadata, string reference, string method)
    {
        SchemaProject("Schemas", new XElement("Schema", new XAttribute("Include", @"Sub\Invoice.xsd"), explicitMetadata
            ? new object[] { new XElement("Namespace", "Explicit.Messages"), new XElement("TypeName", "Renamed") } : []));
        var report = await Analyze(reference);
        var evidence = Assert.Single(report.MapInventory!.Candidates).SourceSchema!;
        Assert.Equal("SourceCandidate", evidence.Status);
        Assert.Equal(reference, evidence.Reference);
        var candidate = Assert.Single(evidence.Candidates);
        Assert.Equal(Path.Combine(_directory, "Schemas", "Schemas.btproj"), candidate.SourceProjectPath);
        Assert.Equal(Path.Combine(_directory, "Schemas", "Sub", "Invoice.xsd"), candidate.SourcePath);
        Assert.Equal(method, candidate.Method);
        Assert.True(candidate.SourceLine > 0);
        Assert.True(candidate.SourceExists);
        Assert.DoesNotContain(report.Artifacts, artifact => artifact.Status == MigrationTranslationStatus.FullyTranslated);
        var restored = JsonSerializer.Deserialize<FlowMigrationReport>(JsonSerializer.Serialize(report, Options), Options)!;
        Assert.Equal(candidate, restored.MapInventory!.Candidates.Single().SourceSchema!.Candidates.Single());
        var view = JsonSerializer.Deserialize<FlowMigrationReportViewModel>(JsonSerializer.Serialize(restored, Options), Options)!;
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<MigrationReport>(ParameterView.FromDictionary(
            new Dictionary<string, object?> { [nameof(MigrationReport.Report)] = view }))).ToHtmlString());
        Assert.Contains("Source schema source evidence: SourceCandidate", html);
        Assert.Contains("not evaluated MSBuild output", html);
        Assert.Contains(candidate.SourcePath, html);
        Assert.Contains("execution blocked", html);
    }

    [Theory]
    [InlineData("contracts.Sub.Invoice")]
    [InlineData("Invoice")]
    [InlineData("Contracts.Sub.Invoice, SomeAssembly")]
    public async Task ClassNames_AreNotMatchedByFilenameOrCaseInsensitiveFallback(string reference)
    {
        SchemaProject("Schemas", new XElement("Schema", new XAttribute("Include", @"Sub\Invoice.xsd")));
        var evidence = (await Analyze(reference)).MapInventory!.Candidates.Single().SourceSchema!;
        Assert.Equal("Unresolved", evidence.Status);
        Assert.Empty(evidence.Candidates);
    }

    [Fact]
    public async Task DuplicateClasses_ExposeAllOwnersInsteadOfSelectingFirst()
    {
        SchemaProject("One", new XElement("Schema", new XAttribute("Include", "Invoice.xsd")));
        SchemaProject("Two", new XElement("Schema", new XAttribute("Include", "Invoice.xsd")));
        var evidence = (await Analyze("Contracts.Invoice")).MapInventory!.Candidates.Single().SourceSchema!;
        Assert.Equal("Ambiguous", evidence.Status);
        Assert.Equal(2, evidence.TotalCandidates);
        Assert.Equal(2, evidence.Candidates.Select(candidate => candidate.SourceProjectPath).Distinct().Count());
    }

    [Fact]
    public async Task OrphanDiskSchemas_DoNotBecomeDeclaredClassOwners()
    {
        SchemaProject("Schemas", new XElement("Schema", new XAttribute("Include", "Other.xsd")));
        File.WriteAllText(Path.Combine(_directory, "Schemas", "Invoice.xsd"), "<schema />");
        var evidence = (await Analyze("Contracts.Invoice")).MapInventory!.Candidates.Single().SourceSchema!;
        Assert.Equal("Unresolved", evidence.Status);
        Assert.Empty(evidence.Candidates);
    }

    [Fact]
    public async Task MissingSource_IsNotClaimedAsAvailableSchema()
    {
        SchemaProject("Schemas", new XElement("Schema", new XAttribute("Include", "Invoice.xsd")));
        File.Delete(Path.Combine(_directory, "Schemas", "Invoice.xsd"));
        var evidence = (await Analyze("Contracts.Invoice")).MapInventory!.Candidates.Single().SourceSchema!;
        Assert.Equal("MissingSource", evidence.Status);
        Assert.False(Assert.Single(evidence.Candidates).SourceExists);
    }

    [Theory]
    [InlineData("Condition", "'$(Configuration)' == 'Debug'")]
    [InlineData("Include", "$(SchemaFolder)\\Invoice.xsd")]
    public async Task ConditionalOrDynamicItems_ProduceVisibleIncompleteEvidence(string attribute, string value)
    {
        var item = new XElement("Schema", new XAttribute("Include", "Invoice.xsd"));
        item.SetAttributeValue(attribute, value);
        SchemaProject("Unsupported", item);
        SchemaProject("Known", new XElement("Schema", new XAttribute("Include", "Invoice.xsd")));
        var report = await Analyze("Contracts.Invoice");
        var evidence = report.MapInventory!.Candidates.Single().SourceSchema!;
        Assert.Equal("IncompleteCatalog", evidence.Status);
        Assert.Single(evidence.Candidates);
        Assert.True(evidence.CatalogWarningCount > 0);
        Assert.NotEmpty(report.MapInventory.SchemaCatalogWarnings);
    }

    [Fact]
    public async Task ItemDefaultsCannotBeSilentlyReplacedWithNamingGuesses()
    {
        var path = SchemaProject("Schemas", new XElement("Schema", new XAttribute("Include", "Invoice.xsd")));
        var document = XDocument.Load(path);
        document.Root!.Add(new XElement("ItemDefinitionGroup", new XElement("Schema", new XElement("Namespace", "Overrides"))));
        document.Save(path);
        var report = await Analyze("Contracts.Invoice");
        Assert.Equal("Unresolved", report.MapInventory!.Candidates.Single().SourceSchema!.Status);
        Assert.Contains(report.MapInventory.SchemaCatalogWarnings, warning => warning.Contains("item defaults", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TargetsAndOtherwiseBranches_AreNotTreatedAsUnconditionalDeclarations(bool target)
    {
        var path = SchemaProject("Schemas", new XElement("Schema", new XAttribute("Include", "Invoice.xsd")));
        var document = XDocument.Load(path);
        var group = document.Root!.Element("ItemGroup")!;
        group.Remove();
        document.Root.Add(target ? new XElement("Target", new XAttribute("Name", "Generate"), group)
            : new XElement("Choose", new XElement("Otherwise", group)));
        document.Save(path);
        var report = await Analyze("Contracts.Invoice");
        Assert.Equal("Unresolved", report.MapInventory!.Candidates.Single().SourceSchema!.Status);
        Assert.Contains(report.MapInventory.SchemaCatalogWarnings, warning => warning.Contains("target-generated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManyDuplicateClasses_RetainCountsWithoutUnboundedSavedCandidates()
    {
        for (var index = 0; index < 34; index++)
            SchemaProject("Schema" + index, new XElement("Schema", new XAttribute("Include", "Invoice.xsd")));
        var evidence = (await Analyze("Contracts.Invoice")).MapInventory!.Candidates.Single().SourceSchema!;
        Assert.Equal("Ambiguous", evidence.Status);
        Assert.Equal(34, evidence.TotalCandidates);
        Assert.Equal(32, evidence.Candidates.Count);
        Assert.Equal(2, evidence.OmittedCount);
    }

    [Fact]
    public async Task TruncatedCatalogCannotClaimUniqueOwnership()
    {
        var path = SchemaProject("Schemas", new XElement("Schema", new XAttribute("Include", "Invoice.xsd")));
        var document = XDocument.Load(path);
        document.Root!.Element("ItemGroup")!.Add(Enumerable.Range(0, 4096).Select(_ => new XElement("Schema", new XAttribute("Include", "Invoice.xsd"))));
        document.Save(path);
        var report = await Analyze("Contracts.Invoice");
        Assert.Equal("IncompleteCatalog", report.MapInventory!.Candidates.Single().SourceSchema!.Status);
        Assert.Contains(report.MapInventory.SchemaCatalogWarnings, warning => warning.Contains("limit exceeded", StringComparison.Ordinal));
    }

    private string SchemaProject(string name, XElement item)
    {
        var directory = Path.Combine(_directory, name);
        Directory.CreateDirectory(directory);
        var include = item.Attribute("Include")!.Value;
        if (!include.Contains('$'))
        {
            var schema = Path.Combine(directory, include);
            Directory.CreateDirectory(Path.GetDirectoryName(schema)!);
            File.WriteAllText(schema, "<schema>Metadata lookup must not compile or execute this text.</schema>");
        }
        var path = Path.Combine(directory, name + ".btproj");
        new XDocument(new XElement("Project", new XElement("PropertyGroup", new XElement("RootNamespace", "Contracts")), new XElement("ItemGroup", item))).Save(path);
        return path;
    }

    private async Task<FlowMigrationReport> Analyze(string reference)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Map.btm"), new XElement("mapsource",
            new XElement("SrcTree", new XElement("Reference", new XAttribute("Location", reference))),
            new XElement("TrgTree", new XAttribute("RootNode_Name", "Target"), new XElement("Reference", new XAttribute("Location", reference)))).ToString());
        new XDocument(new XElement("Project", new XElement("ItemGroup", new XElement("Map", new XAttribute("Include", "Map.btm"))))).Save(Path.Combine(_directory, "Maps.btproj"));
        var analysis = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = _directory }, default);
        return analysis.FlowCandidates.Single(candidate => candidate.SourceProjectPath == Path.Combine(_directory, "Maps.btproj")).DraftFlow.Migration!;
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
