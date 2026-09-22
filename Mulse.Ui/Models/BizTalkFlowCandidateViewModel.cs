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
    BizTalkDraftFlowViewModel DraftFlow)
{
    /// <summary>
    /// Starter C# module skeletons scaffolded for orchestrations with decision or control-flow logic (convoys,
    /// correlations, transactions, loops) too complex to auto-translate into declarative augment rules. Each
    /// can be downloaded individually or as a single zip via the BizTalk migration download endpoints.
    /// </summary>
    public IReadOnlyList<BizTalkScaffoldedModuleViewModel> ScaffoldedModules { get; init; } = [];

    /// <summary>
    /// True when the source orchestration's name looks like a test, debug, or scratch artifact rather than a
    /// production process (e.g. "ImportProcessTestBed"). Advisory only - shown as a badge so a reviewer can
    /// deprioritize or skip migrating it.
    /// </summary>
    public bool IsLikelyTestArtifact { get; init; }
}
