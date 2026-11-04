using Mulse.Modules.Pipeline;
using Service.Models;

namespace Service.BizTalkImport;

public sealed partial class BizTalkImportService
{
    private static BizTalkFlowCandidateResponse AttachRuleEvidence(BizTalkFlowCandidateResponse candidate, IReadOnlyList<BizTalkRulePolicyResponse> policies)
    {
        var report = candidate.DraftFlow.Migration;
        if (report is null) return candidate;
        var names = report.Artifacts.Where(artifact => artifact.Orchestration is not null)
            .SelectMany(artifact => artifact.Orchestration!.Shapes)
            .Where(shape => shape.ShapeType == "CallRules")
            .Select(shape => shape.References.GetValueOrDefault("PolicyName"))
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.Ordinal).ToArray();
        var artifacts = report.Artifacts.ToList();
        var diagnostics = report.Diagnostics.ToList();
        foreach (var name in names)
        {
            var versions = policies.Where(policy => policy.Name == name).ToArray();
            if (versions.Length == 0)
            {
                diagnostics.Add(new("BT-RULE-POLICY-MISSING", MigrationDiagnosticSeverity.Blocking, report.SourceProjectPath,
                    $"No supplied BRL export contains called policy '{name}'.",
                    "Supply the policy/vocabulary export. Call sites do not contain the implementation."));
                continue;
            }
            foreach (var policy in versions)
                artifacts.Add(new("RulePolicy", policy.SourcePath,
                    policy.ProposedPolicyJson is null ? MigrationTranslationStatus.Unsupported : MigrationTranslationStatus.ReviewRequired,
                    [new(policy.ProposedPolicyJson is null ? "BT-RULE-POLICY-UNSUPPORTED" : "BT-RULE-POLICY-REVIEW",
                        policy.ProposedPolicyJson is null ? MigrationDiagnosticSeverity.Blocking : MigrationDiagnosticSeverity.Review, policy.SourcePath,
                        $"Policy '{policy.Name}' {policy.MajorVersion}.{policy.MinorVersion}: {policy.RuleCount} rules ({policy.ActiveRuleCount} active). {string.Join(" ", policy.Diagnostics)}",
                        "Select an explicit version, review all client/group rules and conflicts, construct its XML fact document, and verify CallRules placement and outputs. A proposal is not an executable orchestration translation.")]));
        }
        return candidate with { DraftFlow = candidate.DraftFlow with { Migration = report with { Artifacts = artifacts, Diagnostics = diagnostics } } };
    }
}
