namespace Service.Models;

/// <summary>
/// Results of analyzing one BizTalk orchestration's (.odx) Decision/DecisionBranch shapes and other
/// control-flow signals to decide, per branch, whether it can become a declarative <c>decision-augment</c>
/// rule or needs a scaffolded custom C# module. See <see cref="Service.BizTalkOrchestrationDecisionAnalyzer"/>.
/// </summary>
/// <param name="OrchestrationName">The orchestration's file name (without extension).</param>
/// <param name="GeneratedDecisionJson">
/// A ready-to-use <c>decisionJson</c> value for the <c>decision-augment</c> module, auto-generated from branch
/// expressions simple enough to translate confidently (pure <c>&amp;&amp;</c>/<c>||</c> combinations of field
/// comparisons against string/boolean/null literals). Null when no branch qualified.
/// </param>
/// <param name="ComplexBranches">Decision branches whose expression was too complex to auto-translate.</param>
/// <param name="ComplexControlFlowShapeCounts">
/// Counts, by shape type, of other control-flow constructs (convoys, correlation, atomic transactions, loops)
/// detected in this specific orchestration file.
/// </param>
/// <param name="ScaffoldedModuleSourceCode">
/// Starter C# module source generated when <see cref="ComplexBranches"/> or
/// <see cref="ComplexControlFlowShapeCounts"/> is non-empty. Null otherwise.
/// </param>
public sealed record BizTalkOrchestrationDecisionAnalysisResponse(
    string OrchestrationName,
    string? GeneratedDecisionJson,
    IReadOnlyList<BizTalkComplexDecisionBranchResponse> ComplexBranches,
    IReadOnlyDictionary<string, int> ComplexControlFlowShapeCounts,
    string? ScaffoldedModuleSourceCode,
    string? ScaffoldedModuleFileName,
    string? ScaffoldedModuleId);
