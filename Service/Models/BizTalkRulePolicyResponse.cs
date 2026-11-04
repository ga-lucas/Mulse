namespace Service.Models;

public sealed record BizTalkRulePolicyResponse(
    string SourcePath,
    string Name,
    int MajorVersion,
    int MinorVersion,
    int RuleCount,
    int ActiveRuleCount,
    IReadOnlyList<string> FactTypes,
    string? ProposedPolicyJson,
    IReadOnlyList<string> Diagnostics);
