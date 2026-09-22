namespace Service.Models;

/// <summary>Represents one BizTalk project and its major artifact counts.</summary>
public sealed record BizTalkProjectResponse(
    string Name,
    string ProjectPath,
    int OrchestrationCount,
    int MapCount,
    int SchemaCount,
    int PipelineCount,
    IReadOnlyList<string> ReferencedCustomAssemblies,
    IReadOnlyList<BizTalkArtifactResponse> Artifacts)
{
    /// <summary>
    /// Artifacts (schemas, pipelines, maps, orchestrations) owned by other BizTalk projects that this
    /// project references, resolved transitively. Orchestration-only projects commonly depend on separate
    /// schema or pipeline library projects instead of shipping those artifacts directly, so draft-flow
    /// heuristics (parse/render module selection, schema path population) should consider this list in
    /// addition to <see cref="Artifacts"/> when deciding how to shape the imported draft flow.
    /// </summary>
    public IReadOnlyList<BizTalkArtifactResponse> ReferencedArtifacts { get; init; } = [];

    /// <summary>
    /// Control-flow complexity signals (convoys, parallel branches, correlation sets, atomic transactions)
    /// detected across this project's orchestrations. Empty for projects without orchestrations, or when no
    /// recognized shape types were found. See <see cref="OrchestrationControlFlowSignalResponse"/>.
    /// </summary>
    public IReadOnlyList<OrchestrationControlFlowSignalResponse> OrchestrationControlFlowSignals { get; init; } = [];

    /// <summary>
    /// Per-orchestration Decision/DecisionBranch classification results (which branches were auto-translated
    /// into declarative decision-augment rules versus scaffolded as a starter custom module). Empty for
    /// projects without orchestrations, or when the orchestration's designer metadata couldn't be parsed.
    /// See <see cref="BizTalkOrchestrationDecisionAnalysisResponse"/>.
    /// </summary>
    public IReadOnlyList<BizTalkOrchestrationDecisionAnalysisResponse> OrchestrationDecisionAnalyses { get; init; } = [];

    /// <summary>
    /// Per-orchestration breakdown of <see cref="OrchestrationControlFlowSignals"/>, one entry per orchestration
    /// artifact. Used to split a project with multiple orchestrations into one draft flow candidate per
    /// orchestration (each with its own decision rules/scaffolded modules) instead of collapsing every
    /// orchestration's control-flow logic into a single flow. Empty for projects without orchestrations.
    /// </summary>
    public IReadOnlyList<BizTalkOrchestrationSignalsResponse> OrchestrationSignalsByOrchestration { get; init; } = [];

    /// <summary>
    /// Number of this project's pipeline artifacts classified as receive-direction (Decode/Disassemble/Validate
    /// stages present) by inspecting the pipeline's well-known BizTalk stage category GUIDs. Projects with only
    /// receive pipelines and no orchestration or send pipeline are inbound-only interfaces (a producer, not a
    /// two-way integration) - see <see cref="SendPipelineCount"/>.
    /// </summary>
    public int ReceivePipelineCount { get; init; }

    /// <summary>
    /// Number of this project's pipeline artifacts classified as send-direction (PreAssemble/Assemble/Encode
    /// stages present). See <see cref="ReceivePipelineCount"/>.
    /// </summary>
    public int SendPipelineCount { get; init; }

    /// <summary>
    /// Best-effort XSLT translation results for this project's BizTalk map (.btm) artifacts. See
    /// <see cref="Service.BizTalkImport.BizTalkMapAnalyzer"/>. Empty for projects without map artifacts, or when none of a
    /// project's maps could be parsed.
    /// </summary>
    public IReadOnlyList<BizTalkMapAnalysisResponse> MapAnalyses { get; init; } = [];
}
