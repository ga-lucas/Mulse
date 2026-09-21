namespace Mulse.Modules;

/// <summary>How the delay between retry attempts grows as attempts accumulate.</summary>
public enum RetryBackoffKind
{
    /// <summary>Every retry waits the same configured delay.</summary>
    Fixed,

    /// <summary>Delay grows linearly: attempt * delay (capped by MaxDelay, if set).</summary>
    Linear,

    /// <summary>Delay grows exponentially: delay * 2^(attempt-1) (capped by MaxDelay, if set).</summary>
    Exponential
}
