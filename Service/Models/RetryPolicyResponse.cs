using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents the effective or configured retry policy for a flow (default) or a step (override).</summary>
public sealed record RetryPolicyResponse(
    int? MaxAttempts,
    double DelaySeconds,
    RetryBackoffKind Backoff,
    double? MaxDelaySeconds);
