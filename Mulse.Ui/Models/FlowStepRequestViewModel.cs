namespace Mulse.Ui.Models;

public sealed record FlowStepRequestViewModel(
    string Module,
    Dictionary<string, string> Settings,
    RetryPolicyRequestViewModel? Retry = null);
