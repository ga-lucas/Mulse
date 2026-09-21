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
    IReadOnlyList<string> Warnings);
