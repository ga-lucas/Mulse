namespace Mulse.Ui.Models;

public sealed record BizTalkProjectViewModel(
    string Name,
    string ProjectPath,
    int OrchestrationCount,
    int MapCount,
    int SchemaCount,
    int PipelineCount,
    IReadOnlyList<string> ReferencedCustomAssemblies,
    IReadOnlyList<BizTalkArtifactViewModel> Artifacts)
{
    /// <summary>Artifacts owned by referenced BizTalk projects (e.g. shared schema/pipeline libraries).</summary>
    public IReadOnlyList<BizTalkArtifactViewModel> ReferencedArtifacts { get; init; } = [];

    /// <summary>Control-flow complexity signals (convoys, parallel branches, correlations, transactions).</summary>
    public IReadOnlyList<OrchestrationControlFlowSignalViewModel> OrchestrationControlFlowSignals { get; init; } = [];
}
