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
    BizTalkDraftFlowResponse DraftFlow)
{
    /// <summary>
    /// Starter C# module source files scaffolded for orchestrations with decision branches or control-flow
    /// constructs (convoys, correlation, transactions, loops) too complex to auto-translate into declarative
    /// augment rules. Each compiles as-is (pass-through) and is annotated with TODOs quoting the original
    /// BizTalk logic; save, implement, and register as a plugin module before enabling the flow.
    /// </summary>
    public IReadOnlyList<BizTalkScaffoldedModuleResponse> ScaffoldedModules { get; init; } = [];

    /// <summary>
    /// True when the orchestration this candidate was derived from looks like a test, debug, or scratch
    /// artifact rather than a production process (name-based heuristic, e.g. "ImportProcessTestBed"). Real
    /// BizTalk projects commonly keep such orchestrations checked in alongside production ones; flagging them
    /// lets a reviewer deprioritize or skip migrating them instead of treating every split flow as equally
    /// production-critical. This is advisory only - the candidate is still generated in full.
    /// </summary>
    public bool IsLikelyTestArtifact { get; init; }
}
