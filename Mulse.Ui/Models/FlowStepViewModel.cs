namespace Mulse.Ui.Models;

public sealed record FlowStepViewModel(
    string Module,
    IReadOnlyDictionary<string, string> Settings,
    RetryPolicyViewModel? Retry = null);
