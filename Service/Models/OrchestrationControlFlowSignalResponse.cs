namespace Service.Models;

/// <summary>
/// A heuristic signal about BizTalk orchestration control-flow complexity (for example convoys, parallel
/// branches, correlation sets, or atomic transactions) detected in a project's .odx orchestration files.
/// These counts are produced by a lightweight scan of orchestration designer metadata; full ODX control-flow
/// translation remains out of scope for the importer (see the BizTalk migration warnings).
/// </summary>
public sealed record OrchestrationControlFlowSignalResponse(string ShapeType, string Description, int Count);
