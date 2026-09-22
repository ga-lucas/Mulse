namespace Service.Flows;

public enum FlowExecutionOutcome
{
    Completed,
    Suspended,

    /// <summary>
    /// A step failed but its retry policy allows another attempt. The attempt has been scheduled and persisted to
    /// disk; a background driver will resume it automatically (even across a service restart).
    /// </summary>
    Retrying
}
