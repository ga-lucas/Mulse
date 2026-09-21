namespace Mulse.Ui.Models;

public sealed record BizTalkProjectViewModel(
    string Name,
    string ProjectPath,
    int OrchestrationCount,
    int MapCount,
    int SchemaCount,
    int PipelineCount,
    IReadOnlyList<string> ReferencedCustomAssemblies,
    IReadOnlyList<BizTalkArtifactViewModel> Artifacts);
