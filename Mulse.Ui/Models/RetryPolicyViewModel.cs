namespace Mulse.Ui.Models;

/// <summary>Effective or configured retry policy for a flow (default) or an individual step (override).</summary>
public sealed record RetryPolicyViewModel(
    int? MaxAttempts,
    double DelaySeconds,
    string Backoff,
    double? MaxDelaySeconds);
