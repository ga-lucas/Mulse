namespace Mulse.Modules.Pipeline;

public sealed class ModuleStepDefinition
{
    public string Module { get; init; } = string.Empty;

    /// <summary>
    /// Optional user-defined display name for this node instance (e.g. "Orders SFTP fetch"). Purely cosmetic -
    /// it has no effect on execution and is distinct from any technical id used to wire the flow graph together
    /// (such as a fetch/parse node's id). Falls back to a derived label when unset.
    /// </summary>
    public string? Name { get; init; }

    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Optional retry policy for this specific step. When <c>null</c>, the owning flow's
    /// <see cref="PipelineDefinition.Retry"/> default applies; when that is also <c>null</c>, the step gets exactly
    /// one attempt (no retries), matching pre-existing behavior.
    /// </summary>
    public RetryPolicyDefinition? Retry { get; init; }
}
