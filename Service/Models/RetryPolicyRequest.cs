using System.ComponentModel.DataAnnotations;
using Mulse.Modules;

namespace Service.Models;

/// <summary>Configurable retry policy for a flow (default) or an individual step (override).</summary>
public sealed record RetryPolicyRequest
{
    /// <summary>Maximum attempts including the first. Omit or set null for infinite retries.</summary>
    public int? MaxAttempts { get; init; }

    /// <summary>Base delay, in seconds, between a failed attempt and the next attempt.</summary>
    [Range(0, double.MaxValue)]
    public double DelaySeconds { get; init; } = 5;

    /// <summary>How the delay grows across attempts.</summary>
    public RetryBackoffKind Backoff { get; init; } = RetryBackoffKind.Fixed;

    /// <summary>Optional ceiling, in seconds, on the computed delay.</summary>
    public double? MaxDelaySeconds { get; init; }
}
