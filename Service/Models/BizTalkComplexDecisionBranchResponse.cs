namespace Service.Models;

/// <summary>
/// A single BizTalk orchestration Decision/DecisionBranch shape whose <see cref="Expression"/> could not be
/// confidently translated into a declarative <c>decision-augment</c> rule (for example because it contains
/// method calls, non-comparison operators, or logic beyond simple field comparisons). Reported so the branch's
/// original BizTalk expression is preserved for manual review or inclusion in a scaffolded custom module.
/// </summary>
public sealed record BizTalkComplexDecisionBranchResponse(string DecisionName, string BranchName, string Expression);
