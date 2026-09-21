namespace Mulse.Ui.Models;

/// <summary>Configurable retry policy submitted for a flow (default) or an individual step (override).</summary>
public sealed record RetryPolicyRequestViewModel(
    int? MaxAttempts,
    double DelaySeconds,
    string Backoff,
    double? MaxDelaySeconds);
