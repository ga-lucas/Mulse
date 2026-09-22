using Service.Models;

namespace Service.Tests;

/// <summary>
/// Regression coverage for the "spurious library-only flow candidate" gap: a project with zero
/// orchestrations/maps/pipelines and no related binding must NOT be treated as a flow candidate, while
/// each of orchestration/map/pipeline counts (and a matching binding) independently makes it one.
/// </summary>
public class BizTalkImportServiceShouldCreateFlowCandidateTests
{
    private static BizTalkProjectResponse BuildProject(int orchestrationCount = 0, int mapCount = 0, int pipelineCount = 0)
        => new(
            Name: "Test.Project",
            ProjectPath: @"C:\repo\Test.Project\Test.Project.btproj",
            OrchestrationCount: orchestrationCount,
            MapCount: mapCount,
            SchemaCount: 0,
            PipelineCount: pipelineCount,
            ReferencedCustomAssemblies: [],
            Artifacts: []);

    [Fact]
    public void ReturnsFalse_ForLibraryOnlyProject_WithNoOrchestrationsMapsPipelinesOrBindings()
    {
        // This is the regression case for the "Custom.UHS spurious candidate" bug: a pure schema-library
        // project (no orchestrations/maps/pipelines) with no related binding file must not become a candidate.
        var project = BuildProject();

        var result = BizTalkImportService.ShouldCreateFlowCandidate(project, []);

        Assert.False(result);
    }

    [Fact]
    public void ReturnsTrue_WhenOrchestrationCountIsPositive()
    {
        var project = BuildProject(orchestrationCount: 1);

        Assert.True(BizTalkImportService.ShouldCreateFlowCandidate(project, []));
    }

    [Fact]
    public void ReturnsTrue_WhenMapCountIsPositive()
    {
        var project = BuildProject(mapCount: 1);

        Assert.True(BizTalkImportService.ShouldCreateFlowCandidate(project, []));
    }

    [Fact]
    public void ReturnsTrue_WhenPipelineCountIsPositive()
    {
        // This is the intentional new inclusion signal added alongside the zero-candidate-fallback fix:
        // a project with real pipeline artifacts represents genuine inbound/outbound behavior.
        var project = BuildProject(pipelineCount: 1);

        Assert.True(BizTalkImportService.ShouldCreateFlowCandidate(project, []));
    }

    [Fact]
    public void ReturnsFalse_WhenAllCountsAreZero_EvenWithUnrelatedBindingFiles()
    {
        var project = BuildProject();
        var unrelatedBinding = new BizTalkImportService.BindingFileAnalysis(
            "OtherProject.BindingInfo.xml",
            @"C:\repo\OtherProject\OtherProject.BindingInfo.xml",
            [],
            []);

        var result = BizTalkImportService.ShouldCreateFlowCandidate(project, [unrelatedBinding]);

        Assert.False(result);
    }
}
