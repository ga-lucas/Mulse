namespace Mulse.Ui.Models;

public sealed record BizTalkSolutionAnalysisViewModel(
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
    IReadOnlyList<BizTalkBindingFileViewModel> BindingFiles,
    IReadOnlyList<BizTalkProjectViewModel> BizTalkProjects,
    IReadOnlyList<BizTalkCustomAssemblyViewModel> CustomAssemblies,
    IReadOnlyList<BizTalkFlowCandidateViewModel> FlowCandidates,
    IReadOnlyList<string> Warnings)
{
    public IReadOnlyList<BizTalkRulePolicyViewModel> RulePolicies { get; init; } = [];
    public IReadOnlyList<BizTalkDerivedMapChoiceViewModel> DerivedMapChoices { get; init; } = [];
    public string? EffectiveMapChoicesJson { get; init; }
}

public sealed record BizTalkDerivedMapChoiceViewModel(
    string Value,
    string Status,
    string? MapPath,
    string Detail,
    IReadOnlyList<string> Policies,
    IReadOnlyList<string> Warnings);
