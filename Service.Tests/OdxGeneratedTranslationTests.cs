using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Service.Models;

namespace Service.Tests;

public sealed class OdxGeneratedTranslationTests
{
    [Fact]
    public async Task ImportedAutomaticSourceCompilesAgainstSdk_AndUntranslatedHelpersStayFailExplicit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-auto-odx-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var odx = Path.Combine(directory, "Example.odx");
            await File.WriteAllTextAsync(odx, """
                <?xml version="1.0"?>
                <om:MetaModel xmlns:om="http://schemas.microsoft.com/BizTalk/2003/DesignerData">
                  <om:Element Type="Module"><om:Element Type="ServiceDeclaration">
                    <om:Property Name="Name" Value="Example"/>
                    <om:Element Type="ServiceBody"><om:Element Type="Decision">
                      <om:Element Type="DecisionBranch"><om:Property Name="Expression" Value="count &gt; 0 &amp;&amp; facts.Enabled == true"/>
                        <om:Element Type="VariableAssignment"><om:Property Name="Expression" Value="count++;"/></om:Element>
                        <om:Element Type="MessageAssignment"><om:Property Name="Expression" Value="LegacyComponent.Convert(message);"/></om:Element>
                      </om:Element>
                    </om:Element></om:Element>
                  </om:Element></om:Element>
                </om:MetaModel>
                """);
            var project = Path.Combine(directory, "Example.btproj");
            await File.WriteAllTextAsync(project, "<Project><ItemGroup><XLang Include='Example.odx'/></ItemGroup></Project>");
            var analysis = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project }, default);
            var candidate = Assert.Single(analysis.FlowCandidates); Assert.False(candidate.DraftFlow.Enabled); Assert.NotNull(candidate.DraftFlow.Migration);
            var source = Assert.Single(candidate.ScaffoldedModules, artifact => artifact.ModuleId.StartsWith("odx-translated-", StringComparison.Ordinal) && artifact.FileName.EndsWith(".cs", StringComparison.Ordinal));
            var manifest = Assert.Single(candidate.ScaffoldedModules, artifact => artifact.FileName.EndsWith(".translation.json", StringComparison.Ordinal));
            using var json = JsonDocument.Parse(manifest.SourceCode);
            var diagnostics = json.RootElement.GetProperty("diagnostics").EnumerateArray().ToArray();
            Assert.Equal(2, diagnostics.Count(diagnostic => diagnostic.GetProperty("status").GetString() == "Translated"));
            var helper = Assert.Single(diagnostics, diagnostic => diagnostic.GetProperty("status").GetString() == "RequiresBinding");
            Assert.Equal("MessageAssignment", helper.GetProperty("shapeType").GetString()); Assert.True(helper.GetProperty("sourceLine").GetInt32() > 0);
            Assert.DoesNotContain("LegacyComponent.Convert", manifest.SourceCode);
            await File.WriteAllTextAsync(Path.Combine(directory, source.FileName), source.SourceCode);
            var buildProject = Path.Combine(directory, "Generated.csproj");
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("ImplicitUsings", "disable")),
                new XElement("ItemGroup", new XElement("Reference", new XAttribute("Include", "Mulse.Modules"),
                    new XElement("HintPath", typeof(IOrchestrationAugmentModule).Assembly.Location), new XElement("Private", "false")))))
                .Save(buildProject);
            var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in new[] { "build", buildProject, "--verbosity", "quiet", "--disable-build-servers" }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
            var assembly = Assembly.Load(File.ReadAllBytes(Path.Combine(directory, "bin", "Debug", "net10.0", "Generated.dll")));
            var module = (IOrchestrationAugmentModule)Activator.CreateInstance(Assert.Single(assembly.GetTypes(), type => typeof(IOrchestrationAugmentModule).IsAssignableFrom(type)))!;
            var failure = await Assert.ThrowsAsync<NotSupportedException>(() => module.AugmentAsync(null!, IntegrationBatch.Empty, null!, default));
            Assert.Contains("no verified", failure.Message);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.AugmentAsync(null!, IntegrationBatch.Empty, null!, new CancellationToken(true)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("invalid designer source")]
    [InlineData("<?xml version='1.0'?><MetaModel xmlns='http://schemas.microsoft.com/BizTalk/2003/DesignerData'><Element Type='While'/></MetaModel>")]
    public async Task UnsupportedSourceProducesExplicitBlockingArtifact_NotAnEmptySuccessfulTranslation(string source)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, source);
            var artifact = Assert.Single(Service.BizTalkImport.BizTalkOdxTranslationScaffolder.Create(path));
            Assert.EndsWith(".translation.json", artifact.FileName);
            using var json = JsonDocument.Parse(artifact.SourceCode);
            Assert.Equal("Blocked", json.RootElement.GetProperty("status").GetString());
        }
        finally { File.Delete(path); }
    }
}
