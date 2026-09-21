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
}
