namespace Service.Models;

/// <summary>
/// Control-flow shape signals (decisions, correlations, convoys, transactions, loops) detected for a single
/// orchestration within a BizTalk project. Used both to report per-orchestration complexity and to drive
/// splitting a multi-orchestration project into one draft flow per orchestration instead of collapsing every
/// orchestration's logic into a single flow. See <see cref="OrchestrationControlFlowSignalResponse"/>.
/// </summary>
public sealed record BizTalkOrchestrationSignalsResponse(
    string OrchestrationName,
    IReadOnlyList<OrchestrationControlFlowSignalResponse> Signals);
