using System.Text.Json;
using System.Xml.Linq;
using Mulse.Modules.Pipeline;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkOrchestrationInventoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-odx-" + Guid.NewGuid().ToString("N"));
    private const string Namespace = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";

    [Fact]
    public async Task Inventory_PreservesDesignerOrderHierarchyCountsAndOriginalSourceLines_WithoutExpressionBodies()
    {
        var source = "// #if __DESIGNER_DATA\n<?xml version=\"1.0\"?>\n" +
            $"<om:MetaModel xmlns:om=\"{Namespace}\">\n" +
            "  <om:Element Type=\"ServiceDeclaration\">\n" +
            "    <om:Property Name=\"Name\" Value=\"Process\" />\n" +
            "    <om:Element Type=\"While\">\n" +
            "      <om:Property Name=\"Name\" Value=\"Process items\" />\n" +
            "      <om:Element Type=\"Send\">\n" +
            "        <om:Property Name=\"PortName\" Value=\"Output\" />\n" +
            "        <om:Property Name=\"MessageName\" Value=\"Message\" />\n" +
            "        <om:Property Name=\"Expression\" Value=\"sensitive-expression-not-persisted\" />\n" +
            "      </om:Element>\n    </om:Element>\n  </om:Element>\n</om:MetaModel>\n// #endif\nType=\"While\"";
        var result = await Analyze(source);
        var artifact = Artifact(result);
        var inventory = Assert.IsType<OrchestrationMigrationInventory>(artifact.Orchestration);
        Assert.Equal(3, inventory.Shapes.Count);
        Assert.Equal(new[] { "ServiceDeclaration", "While", "Send" }, inventory.Shapes.Select(shape => shape.ShapeType));
        Assert.Equal(new[] { 4, 6, 8 }, inventory.Shapes.Select(shape => shape.SourceLine));
        Assert.Equal(new int?[] { null, 1, 2 }, inventory.Shapes.Select(shape => shape.ParentId));
        Assert.Equal(1, inventory.ShapeCounts["While"]);
        Assert.Equal("Output", inventory.Shapes[2].References["PortName"]);
        Assert.Equal("Message", inventory.Shapes[2].References["MessageName"]);
        Assert.DoesNotContain("sensitive-expression-not-persisted", JsonSerializer.Serialize(artifact));
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Code == "BT-ODX-LOOPS");
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Code == "BT-ODX-PORTS");
        Assert.Equal(MigrationTranslationStatus.StubGenerated, artifact.Status);
        Assert.Contains(result.BizTalkProjects.Single().OrchestrationDecisionAnalyses, analysis => analysis.ComplexControlFlowShapeCounts.ContainsKey("While"));
    }

    [Theory]
    [InlineData("MessageAssignment", "BT-ODX-ASSIGNMENTS")]
    [InlineData("VariableAssignment", "BT-ODX-ASSIGNMENTS")]
    [InlineData("CallRules", "BT-ODX-RULES")]
    [InlineData("While", "BT-ODX-LOOPS")]
    [InlineData("Loop", "BT-ODX-LOOPS")]
    [InlineData("Receive", "BT-ODX-PORTS")]
    [InlineData("Send", "BT-ODX-PORTS")]
    [InlineData("Call", "BT-ODX-PORTS")]
    [InlineData("Catch", "BT-ODX-FAILURE-SCOPES")]
    [InlineData("AtomicTransaction", "BT-ODX-FAILURE-SCOPES")]
    [InlineData("Delay", "BT-ODX-WAITS")]
    [InlineData("CorrelationDeclaration", "BT-ODX-WAITS")]
    public async Task UnsupportedBehavior_ProducesExplicitBlockingRemediation(string type, string code)
    {
        var result = await Analyze(Designer(new XElement(XName.Get("Element", Namespace), new XAttribute("Type", type))));
        var artifact = Artifact(result);
        var diagnostic = Assert.Single(artifact.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.Equal(MigrationDiagnosticSeverity.Blocking, diagnostic.Severity);
        Assert.Equal(artifact.SourcePath, diagnostic.SourcePath);
        Assert.Contains(type, diagnostic.Message);
        Assert.False(string.IsNullOrWhiteSpace(diagnostic.Remediation));
        Assert.False(result.FlowCandidates.Single().DraftFlow.Enabled);
    }

    [Theory]
    [InlineData("new")]
    [InlineData("")]
    public async Task Inventory_UsesNamespaceRatherThanFixedPrefix_AndRetainsUnknownShapes(string prefix)
    {
        var tag = prefix.Length == 0 ? "" : prefix + ":";
        var xmlns = prefix.Length == 0 ? "xmlns" : "xmlns:" + prefix;
        var source = $"<?xml version=\"1.0\"?><{tag}MetaModel {xmlns}=\"{Namespace}\"><{tag}Element Type=\"FutureShape\" /></{tag}MetaModel>";
        var artifact = Artifact(await Analyze(source));
        var inventory = Assert.IsType<OrchestrationMigrationInventory>(artifact.Orchestration);
        Assert.Equal(1, inventory.ShapeCounts["FutureShape"]);
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
        Assert.NotEqual(MigrationTranslationStatus.FullyTranslated, artifact.Status);
    }

    [Theory]
    [InlineData("<Root />")]
    [InlineData("<?xml version=\"1.0\"?><om:MetaModel xmlns:om=\"wrong\"><om:Element Type=\"Send\" /></om:MetaModel>")]
    [InlineData("<?xml version=\"1.0\"?><om:MetaModel xmlns:om=\"http://schemas.microsoft.com/BizTalk/2003/DesignerData\"></om:MetaModel>")]
    [InlineData("<?xml version=\"1.0\"?><om:MetaModel><om:Element Type=\"Send\"></om:MetaModel>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE MetaModel [<!ENTITY x 'expanded'>]><om:MetaModel xmlns:om=\"http://schemas.microsoft.com/BizTalk/2003/DesignerData\"><om:Element Type=\"Send\" /></om:MetaModel>")]
    public async Task InvalidOrUnsafeMetadata_IsExplicitlyNotAnalyzed(string source)
    {
        var artifact = Artifact(await Analyze(source));
        Assert.Null(artifact.Orchestration);
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Code == "BT-ODX-NOT-ANALYZED" && diagnostic.Severity == MigrationDiagnosticSeverity.Blocking);
    }

    [Fact]
    public async Task ExcessiveSource_IsReportedInsteadOfTruncatedIntoSuccess()
    {
        var artifact = Artifact(await Analyze(Designer(new XElement(XName.Get("Element", Namespace), new XAttribute("Type", "Send"))) + new string(' ', 8_000_001)));
        Assert.Null(artifact.Orchestration);
        Assert.Contains(artifact.Diagnostics, diagnostic => diagnostic.Code == "BT-ODX-NOT-ANALYZED" && diagnostic.Message.Contains("limit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SplitCandidates_KeepOnlyTheirOwnSourceInventory()
    {
        await Analyze(Designer(new XElement(XName.Get("Element", Namespace), new XAttribute("Type", "While"))));
        File.WriteAllText(Path.Combine(_directory, "Other.odx"), Designer(new XElement(XName.Get("Element", Namespace), new XAttribute("Type", "CallRules"))));
        WriteProject("Import.btproj", "Process.odx", "Other.odx");
        var result = await AnalyzePath(Path.Combine(_directory, "Import.btproj"));
        Assert.Equal(2, result.FlowCandidates.Count);
        foreach (var candidate in result.FlowCandidates)
        {
            var artifact = Assert.Single(candidate.DraftFlow.Migration!.Artifacts);
            var expected = candidate.DraftFlow.Migration.SourceOrchestration == "Process" ? "While" : "CallRules";
            Assert.Equal(expected, Assert.Single(artifact.Orchestration!.Shapes).ShapeType);
        }
    }

    [Fact]
    public async Task OdxPath_ResolvesExactOwnerAndRetainsSiblingCandidates()
    {
        await Analyze(Designer(new XElement(XName.Get("Element", Namespace), new XAttribute("Type", "Send"))));
        File.WriteAllText(Path.Combine(_directory, "Other.odx"), Designer(new XElement(XName.Get("Element", Namespace), new XAttribute("Type", "Delay"))));
        WriteProject("Import.btproj", @".\Process.odx", "Other.odx");
        WriteProject("Unrelated.btproj", "Unrelated.odx");
        var result = await AnalyzePath(Path.Combine(_directory, "Process.odx"));
        Assert.Equal("Orchestration", result.SourceType);
        Assert.Equal(2, result.FlowCandidates.Count);
        Assert.Single(result.BizTalkProjects);
        Assert.All(result.FlowCandidates, candidate => Assert.Equal(Path.Combine(_directory, "Import.btproj"), candidate.SourceProjectPath));
        Assert.Contains(result.Warnings, warning => warning.Contains("owning project", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OdxPath_RejectsAmbiguousOwnersRatherThanChoosingFirst()
    {
        await Analyze(Designer(new XElement(XName.Get("Element", Namespace), new XAttribute("Type", "Send"))));
        WriteProject("Second.btproj", "Process.odx");
        var error = await Assert.ThrowsAsync<ArgumentException>(() => AnalyzePath(Path.Combine(_directory, "Process.odx")));
        Assert.Contains("Multiple sibling", error.Message);
        Assert.Contains("Second.btproj", error.Message);
        Assert.Contains("Import.btproj", error.Message);
    }

    [Fact]
    public async Task OdxPath_RejectsMissingOwnerInsteadOfGuessingByFilename()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Process.odx"), "not standalone");
        WriteProject("Process.btproj", "Other.odx");
        var error = await Assert.ThrowsAsync<ArgumentException>(() => AnalyzePath(Path.Combine(_directory, "Process.odx")));
        Assert.Contains("No sibling", error.Message);
        Assert.Contains("owning .btproj", error.Message);
    }

    [Fact]
    public async Task OdxPath_ReportsUnreadableOwnerCandidateWithoutPretendingOwnershipIsUnique()
    {
        await Analyze(Designer(new XElement(XName.Get("Element", Namespace), new XAttribute("Type", "Send"))));
        File.WriteAllText(Path.Combine(_directory, "Broken.btproj"), "<Project>");
        var error = await Assert.ThrowsAsync<ArgumentException>(() => AnalyzePath(Path.Combine(_directory, "Process.odx")));
        Assert.Contains("Unable to inspect", error.Message);
        Assert.Contains("Broken.btproj", error.Message);
    }

    private async Task<BizTalkSolutionAnalysisResponse> Analyze(string source)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "Process.odx"), source);
        WriteProject("Import.btproj", "Process.odx");
        return await AnalyzePath(Path.Combine(_directory, "Import.btproj"));
    }

    private Task<BizTalkSolutionAnalysisResponse> AnalyzePath(string path)
        => new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = path }, CancellationToken.None);

    private void WriteProject(string name, params string[] artifacts)
        => new XDocument(new XElement("Project", new XElement("ItemGroup", artifacts.Select(path => new XElement("XLang", new XAttribute("Include", path))))))
            .Save(Path.Combine(_directory, name));

    private static string Designer(XElement element)
        => "// #if __DESIGNER_DATA\n<?xml version=\"1.0\"?>\n" + new XElement(XName.Get("MetaModel", Namespace), new XAttribute(XNamespace.Xmlns + "om", Namespace), element) + "\n// #endif";

    private static MigrationArtifactReport Artifact(BizTalkSolutionAnalysisResponse result)
        => Assert.Single(Assert.Single(result.FlowCandidates).DraftFlow.Migration!.Artifacts);

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
