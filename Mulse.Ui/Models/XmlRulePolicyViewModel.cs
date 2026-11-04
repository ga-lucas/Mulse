namespace Mulse.Ui.Models;

public sealed record BizTalkRulePolicyViewModel(string SourcePath, string Name, int MajorVersion, int MinorVersion,
    int RuleCount, int ActiveRuleCount, IReadOnlyList<string> FactTypes, string? ProposedPolicyJson, IReadOnlyList<string> Diagnostics);

public sealed class XmlRulePolicyViewModel
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string RootName { get; set; } = "";
    public string RootNamespace { get; set; } = "";
    public List<XmlFieldRuleViewModel> Rules { get; set; } = [];
}

public sealed class XmlFieldRuleViewModel
{
    public string Name { get; set; } = "";
    public bool Active { get; set; } = true;
    public int Priority { get; set; }
    public bool ConstantCondition { get; set; } = true;
    public List<XmlRuleFieldViewModel> Conditions { get; set; } = [];
    public List<XmlRuleFieldViewModel> Assignments { get; set; } = [];
}

public sealed class XmlRuleFieldViewModel
{
    public string Name { get; set; } = "";
    public string Namespace { get; set; } = "";
    public string Type { get; set; } = "string";
    public string Value { get; set; } = "";
}

public sealed record XmlRulePreviewRequestViewModel(string PolicyJson, string InputXml);
public sealed record XmlRulePreviewResultViewModel(string OutputXml, IReadOnlyList<string> MatchedRules);
