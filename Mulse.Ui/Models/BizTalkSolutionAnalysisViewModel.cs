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
    IReadOnlyList<string> Warnings);
