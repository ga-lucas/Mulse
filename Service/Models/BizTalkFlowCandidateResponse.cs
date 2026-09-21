namespace Service.Models;

/// <summary>Represents one draft Mulse migration candidate derived from a BizTalk project.</summary>
public sealed record BizTalkFlowCandidateResponse(
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
    BizTalkDraftFlowResponse DraftFlow);
