namespace Service.Models;

/// <summary>Represents the complete migration analysis result for a BizTalk solution, project, or directory.</summary>
public sealed record BizTalkSolutionAnalysisResponse(
    string SourcePath,
    string SourceType,
    string DisplayName,
    string RootDirectory,
    int BizTalkProjectCount,
    int CustomAssemblyCount,
    int OrchestrationCount,
    int MapCount,
    int SchemaCount,
    int PipelineCount,
    IReadOnlyList<BizTalkBindingFileResponse> BindingFiles,
    IReadOnlyList<BizTalkProjectResponse> BizTalkProjects,
    IReadOnlyList<BizTalkCustomAssemblyResponse> CustomAssemblies,
    IReadOnlyList<BizTalkFlowCandidateResponse> FlowCandidates,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<BizTalkRulePolicyResponse> RulePolicies { get; init; } = [];

    /// <summary>Per-value outcome of deriving native map choices from policy map-name literals; empty unless requested.</summary>
    public IReadOnlyList<BizTalkDerivedMapChoiceResponse> DerivedMapChoices { get; init; } = [];

    /// <summary>The effective render-dispatch casesJson used for native generation (explicit choices plus derived ones); null unless derivation was requested.</summary>
    public string? EffectiveMapChoicesJson { get; init; }
}
