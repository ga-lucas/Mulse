using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkProjectReferenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "Mulse-BizTalkReferences", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DuplicateProjectNames_KeepSeparateArtifactsAndUniqueStableDraftAndConfigIds()
    {
        var firstPath = WriteProject("First", "Partner");
        var secondPath = WriteProject("Second", "Partner");
        var result = await Analyze();
        var repeated = await Analyze();

        Assert.Equal(2, result.BizTalkProjectCount);
        Assert.Equal(2, result.FlowCandidates.Count);
        Assert.Equal(2, result.FlowCandidates.Select(candidate => candidate.SuggestedFlowId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(result.FlowCandidates.Select(candidate => candidate.SuggestedFlowId), repeated.FlowCandidates.Select(candidate => candidate.SuggestedFlowId));
        Assert.Equal(new[] { firstPath, secondPath }, result.FlowCandidates.Select(candidate => candidate.SourceProjectPath));
        Assert.All(result.FlowCandidates, candidate =>
        {
            Assert.Equal(candidate.SuggestedFlowId, candidate.DraftFlow.Id);
            Assert.False(candidate.DraftFlow.Enabled);
            Assert.All(candidate.DraftFlow.ConfigurationRequirements, requirement => Assert.StartsWith(candidate.SuggestedFlowId, requirement.ReferenceName));
        });
        Assert.Empty(result.FlowCandidates[0].DraftFlow.ConfigurationRequirements.Select(requirement => requirement.ReferenceName)
            .Intersect(result.FlowCandidates[1].DraftFlow.ConfigurationRequirements.Select(requirement => requirement.ReferenceName), StringComparer.OrdinalIgnoreCase));
        Assert.All(result.BizTalkProjects, project => Assert.Empty(project.ReferencedArtifacts));
    }

    [Fact]
    public async Task OrchestrationNamesWithSameSlug_ReserveDistinctIdsBeforeCreatingDrafts()
    {
        var projectPath = WriteProject("Owner", "Owner");
        var document = XDocument.Load(projectPath);
        foreach (var name in new[] { "Process-A", "Process_A" })
        {
            document.Root!.Add(new XElement("ItemGroup", new XElement("XLang", new XAttribute("Include", name + ".odx"))));
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, name + ".odx"), "<Root />");
        }
        document.Save(projectPath);

        var result = await Analyze();
        Assert.Equal(2, result.FlowCandidates.Count);
        Assert.Equal(2, result.FlowCandidates.Select(candidate => candidate.SuggestedFlowId).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(result.FlowCandidates, candidate => Assert.Equal(candidate.SuggestedFlowId, candidate.DraftFlow.Id));
        Assert.Contains(result.FlowCandidates[1].DraftFlow.Warnings, warning => warning.Contains("collides", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnresolvedTransitiveReferences_RemainVisibleInDraftFollowUpWarnings()
    {
        var library = WriteProject("Library", "Library", ProjectReference(@"..\Missing\Missing.btproj"));
        var owner = WriteProject("Owner", "Owner", ProjectReference(@"..\Library\Library.btproj"));

        var result = await Analyze();
        var candidate = result.FlowCandidates.Single(candidate => candidate.SourceProjectPath == owner);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains(library, StringComparison.Ordinal)
            && warning.Contains("Missing", StringComparison.Ordinal) && warning.Contains("no name-based substitute", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExplicitProjectReference_UsesPathInsteadOfDuplicateNameOrNameMetadata()
    {
        var target = WriteProject("One", "Shared");
        var other = WriteProject("Two", "Shared");
        var owner = WriteProject("Owner", "Owner", ProjectReference(@"..\One\One.btproj", "WrongName"));

        var result = await Analyze();
        var project = result.BizTalkProjects.Single(project => project.ProjectPath == owner);
        var reference = Assert.Single(project.References);
        Assert.Equal(BizTalkReferenceResolution.ProjectPath, reference.Resolution);
        Assert.Equal(target, reference.ResolvedProjectPath);
        Assert.Contains(project.ReferencedArtifacts, artifact => artifact.FullPath == ArtifactPath(target));
        Assert.DoesNotContain(project.ReferencedArtifacts, artifact => artifact.FullPath == ArtifactPath(other));
    }

    [Theory]
    [InlineData("Shared")]
    [InlineData("Shared, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null")]
    public async Task HintPath_SelectsMatchingVariantWithoutRequiringCompiledDll(string include)
    {
        var target = WriteProject("One", "Shared");
        var other = WriteProject("Two", "Shared");
        var owner = WriteProject("Owner", "Owner", AssemblyReference(include, @"..\One\bin\Debug\Shared.dll"));

        var result = await Analyze();
        var project = result.BizTalkProjects.Single(project => project.ProjectPath == owner);
        var reference = Assert.Single(project.References);
        Assert.Equal(BizTalkReferenceResolution.HintPath, reference.Resolution);
        Assert.Equal(target, reference.ResolvedProjectPath);
        Assert.Contains(project.ReferencedArtifacts, artifact => artifact.FullPath == ArtifactPath(target));
        Assert.DoesNotContain(project.ReferencedArtifacts, artifact => artifact.FullPath == ArtifactPath(other));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(target)!, "bin", "Debug", "Shared.dll")));
    }

    [Fact]
    public async Task AmbiguousName_IsReportedWithoutLeakingArtifactsFromEitherVariant()
    {
        var first = WriteProject("One", "Shared");
        var second = WriteProject("Two", "Shared");
        var owner = WriteProject("Owner", "Owner", AssemblyReference("Shared"));

        var result = await Analyze();
        var project = result.BizTalkProjects.Single(project => project.ProjectPath == owner);
        var reference = Assert.Single(project.References);
        Assert.Equal(BizTalkReferenceResolution.Ambiguous, reference.Resolution);
        Assert.Null(reference.ResolvedProjectPath);
        Assert.Equal(new[] { first, second }, reference.CandidateProjectPaths);
        Assert.Empty(project.ReferencedArtifacts);
        Assert.Contains(result.Warnings, warning => warning.Contains(owner, StringComparison.Ordinal) && warning.Contains("Multiple source projects", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"..\Missing\Missing.btproj", true)]
    [InlineData(@"..\SharedCopy\bin\Shared.dll", false)]
    [InlineData(@"..\Shared\bin\Other.dll", false)]
    [InlineData(@"$(SharedOutput)\Shared.dll", false)]
    public async Task UnresolvedPath_NeverFallsBackToEvenAUniqueMatchingName(string path, bool isProject)
    {
        WriteProject("Shared", "Shared");
        var owner = WriteProject("Owner", "Owner", isProject ? ProjectReference(path, "Shared") : AssemblyReference("Shared", path));

        var result = await Analyze();
        var project = result.BizTalkProjects.Single(project => project.ProjectPath == owner);
        var reference = Assert.Single(project.References);
        Assert.Equal(BizTalkReferenceResolution.Unresolved, reference.Resolution);
        Assert.Null(reference.ResolvedProjectPath);
        Assert.Empty(project.ReferencedArtifacts);
        Assert.Contains(result.Warnings, warning => warning.Contains(owner, StringComparison.Ordinal) && warning.Contains("Shared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UniqueNameFallbackAndExternalReferences_AreExplicitAndBackwardCompatible()
    {
        var target = WriteProject("Shared", "Shared");
        var owner = WriteProject("Owner", "Owner", AssemblyReference("Shared"), AssemblyReference("System.Xml"));

        var result = await Analyze();
        var project = result.BizTalkProjects.Single(project => project.ProjectPath == owner);
        Assert.Equal(BizTalkReferenceResolution.UniqueName, project.References[0].Resolution);
        Assert.Equal(target, project.References[0].ResolvedProjectPath);
        Assert.Equal(BizTalkReferenceResolution.External, project.References[1].Resolution);
        Assert.Null(project.References[1].ResolvedProjectPath);
        Assert.Contains(project.ReferencedArtifacts, artifact => artifact.FullPath == ArtifactPath(target));
    }

    [Fact]
    public async Task CyclesAndDiamondDependencies_IncludeEachArtifactOnceAndExcludeSelf()
    {
        var root = WriteProject("Root", "Root", ProjectReference(@"..\Left\Left.btproj"), ProjectReference(@"..\Right\Right.btproj"));
        var left = WriteProject("Left", "Left", ProjectReference(@"..\Leaf\Leaf.btproj"));
        var right = WriteProject("Right", "Right", ProjectReference(@"..\Leaf\Leaf.btproj"));
        var leaf = WriteProject("Leaf", "Leaf", ProjectReference(@"..\Root\Root.btproj"));

        var result = await Analyze();
        var project = result.BizTalkProjects.Single(project => project.ProjectPath == root);
        Assert.Equal(3, project.ReferencedArtifacts.Count);
        Assert.Equal(new[] { ArtifactPath(left), ArtifactPath(right), ArtifactPath(leaf) }.Order(),
            project.ReferencedArtifacts.Select(artifact => artifact.FullPath).Order());
    }

    [Fact]
    public async Task DuplicateCustomAssemblyNames_AreAttachedByResolvedPathIncludingThroughBizTalkLibrary()
    {
        var firstHelper = WriteProject("FirstHelper", "Helper", custom: true);
        var secondHelper = WriteProject("SecondHelper", "Helper", custom: true);
        var first = WriteProject("First", "Partner", ProjectReference(@"..\FirstHelper\FirstHelper.csproj"));
        var second = WriteProject("Second", "Partner", AssemblyReference("Helper", @"..\SecondHelper\bin\Debug\Helper.dll"));
        var wrapper = WriteProject("Wrapper", "Wrapper", ProjectReference(@"..\First\First.btproj"));

        var result = await Analyze();
        var one = result.CustomAssemblies.Single(assembly => assembly.ProjectPath == firstHelper);
        var two = result.CustomAssemblies.Single(assembly => assembly.ProjectPath == secondHelper);
        Assert.Equal(new[] { first, wrapper }, one.UsedByProjectPaths.Order());
        Assert.Equal(new[] { second }, two.UsedByProjectPaths);
        Assert.All(result.BizTalkProjects, project => Assert.Equal(new[] { "Helper" }, project.ReferencedCustomAssemblies));
        Assert.DoesNotContain(second, one.UsedByProjectPaths);
        Assert.DoesNotContain(first, two.UsedByProjectPaths);
    }

    [Fact]
    public async Task AdditionalScopes_AreDeduplicatedByPathAndRetainOriginalProjectIdentity()
    {
        var target = WriteProject("Shared", "Shared");
        var owner = WriteProject("Owner", "Owner", ProjectReference(@"..\Shared\Shared.btproj"));
        var service = new BizTalkImportService();
        var missing = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = owner }, CancellationToken.None);
        Assert.Equal(BizTalkReferenceResolution.Unresolved, Assert.Single(Assert.Single(missing.BizTalkProjects).References).Resolution);

        var result = await service.AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
        {
            SourcePath = owner,
            AdditionalSourcePaths = [target, _root, target]
        }, CancellationToken.None);
        Assert.Equal(2, result.BizTalkProjectCount);
        Assert.Equal(target, Assert.Single(result.BizTalkProjects.Single(project => project.ProjectPath == owner).References).ResolvedProjectPath);
    }

    [Fact]
    public async Task DependencyReport_DeserializesApiEvidenceAndRendersStatusesPathsAndRemediation()
    {
        WriteProject("One", "Shared");
        WriteProject("Two", "Shared");
        WriteProject("Owner", "Owner", AssemblyReference("Shared"), AssemblyReference("System.Xml"));
        var result = await Analyze();
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        var json = JsonSerializer.Serialize(result, options);
        var model = JsonSerializer.Deserialize<BizTalkSolutionAnalysisViewModel>(json, options)!;
        Assert.All(model.FlowCandidates, candidate => Assert.False(string.IsNullOrWhiteSpace(candidate.SourceProjectPath)));

        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<BizTalkDependencyReport>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(BizTalkDependencyReport.Projects)] = model.BizTalkProjects }));
            return rendered.ToHtmlString();
        });
        Assert.Contains("Project dependencies (3 project(s))", html);
        Assert.Contains("Shared - Ambiguous", html);
        Assert.Contains("System.Xml - External", html);
        Assert.Contains("Supply a ProjectReference or concrete HintPath", html);
        Assert.Contains("Resolution does not imply executable BizTalk compatibility", html);
        Assert.All(model.BizTalkProjects, project => Assert.Contains(project.ProjectPath, html));
    }

    private Task<BizTalkSolutionAnalysisResponse> Analyze()
        => new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = _root }, CancellationToken.None);

    private string WriteProject(string directory, string assembly, params XElement[] references)
        => WriteProject(directory, assembly, custom: false, references);

    private string WriteProject(string directory, string assembly, bool custom, params XElement[] references)
    {
        var folder = Path.Combine(_root, directory);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, directory + (custom ? ".csproj" : ".btproj"));
        var items = new XElement("ItemGroup", references);
        if (!custom)
        {
            items.Add(new XElement("Pipeline", new XAttribute("Include", "Receive.btp")));
            File.WriteAllText(ArtifactPath(path), "<Document />");
        }

        new XDocument(new XElement("Project",
            new XElement("PropertyGroup", new XElement("AssemblyName", assembly)), items)).Save(path);
        return path;
    }

    private static XElement ProjectReference(string path, string? name = null)
        => new("ProjectReference", new XAttribute("Include", path), name is null ? null : new XElement("Name", name));

    private static XElement AssemblyReference(string name, string? hint = null)
        => new("Reference", new XAttribute("Include", name), hint is null ? null : new XElement("HintPath", hint));

    private static string ArtifactPath(string projectPath) => Path.Combine(Path.GetDirectoryName(projectPath)!, "Receive.btp");

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
