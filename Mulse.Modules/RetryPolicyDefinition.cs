namespace Mulse.Modules;

/// <summary>
/// Configurable retry behavior for a single flow step (fetch, parse, an augment, a delivery route's render/deliver/
/// compensation) or as a flow-wide default that steps fall back to when they don't define their own policy.
/// Migrated from BizTalk's per-adapter/per-send-port retry count and retry interval settings, but generalized to
/// every module kind and given optional backoff so slow-to-recover downstream systems don't get hammered.
/// </summary>
public sealed class RetryPolicyDefinition
{
    /// <summary>
    /// Maximum number of attempts (including the first). <c>null</c> means retry forever until it succeeds or the
    /// flow/service is stopped. <c>1</c> (the implicit default when no policy is configured) means no retries.
    /// </summary>
    public int? MaxAttempts { get; init; }

    /// <summary>Base delay between the first failed attempt and the next attempt.</summary>
    public TimeSpan Delay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How the delay grows across successive attempts. Defaults to a fixed delay.</summary>
    public RetryBackoffKind Backoff { get; init; } = RetryBackoffKind.Fixed;

    /// <summary>Optional ceiling on the computed delay, regardless of backoff growth.</summary>
    public TimeSpan? MaxDelay { get; init; }

    /// <summary>A policy that never retries: exactly one attempt. Used when no retry policy is configured anywhere.</summary>
    public static RetryPolicyDefinition NoRetry { get; } = new() { MaxAttempts = 1 };

    /// <summary>Whether another attempt is allowed after <paramref name="attemptsMade"/> attempts have already been made.</summary>
    public bool AllowsAnotherAttempt(int attemptsMade) => MaxAttempts is null || attemptsMade < MaxAttempts.Value;

    /// <summary>Computes the delay to wait before the attempt numbered <paramref name="nextAttemptNumber"/> (2, 3, ...).</summary>
    public TimeSpan ComputeDelay(int nextAttemptNumber)
    {
        var attemptsElapsed = Math.Max(nextAttemptNumber - 1, 1);
        var delay = Backoff switch
        {
            RetryBackoffKind.Fixed => Delay,
            RetryBackoffKind.Linear => Delay * attemptsElapsed,
            RetryBackoffKind.Exponential => Delay * Math.Pow(2, attemptsElapsed - 1),
            _ => Delay
        };

        if (MaxDelay is { } maxDelay && delay > maxDelay)
        {
            delay = maxDelay;
        }

        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }
}
