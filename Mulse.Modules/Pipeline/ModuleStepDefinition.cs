namespace Mulse.Modules.Pipeline;

public sealed class ModuleStepDefinition
{
    public string Module { get; init; } = string.Empty;

    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional retry policy for this specific step. When <c>null</c>, the owning flow's
    /// <see cref="PipelineDefinition.Retry"/> default applies; when that is also <c>null</c>, the step gets exactly
    /// one attempt (no retries), matching pre-existing behavior.
    /// </summary>
    public RetryPolicyDefinition? Retry { get; init; }
}
