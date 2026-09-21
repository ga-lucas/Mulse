namespace Mulse.Ui.Models;

public sealed record BizTalkFlowCandidateViewModel(
    string SuggestedFlowId,
    string SourceProject,
    string Summary,
    string FetchGuidance,
    string ParseGuidance,
    IReadOnlyList<string> AugmentGuidance,
    IReadOnlyList<string> DeliveryGuidance,
    IReadOnlyList<string> OpenQuestions,
    IReadOnlyList<string> SourceArtifacts,
    IReadOnlyList<string> RelatedBindingFiles,
    BizTalkDraftFlowViewModel DraftFlow);
