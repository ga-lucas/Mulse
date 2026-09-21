namespace Mulse.Modules;

public sealed class PipelineDefinition
{
    public string Id { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public PipelineTriggerOptions Trigger { get; init; } = new();

    public ModuleStepDefinition Fetch { get; init; } = new();

    public ModuleStepDefinition Parse { get; init; } = new();

    public List<ModuleStepDefinition> Augments { get; init; } = [];

    public List<DeliveryRouteDefinition> Deliveries { get; init; } = [];

    /// <summary>
    /// Default retry policy applied to every step in this flow that doesn't define its own
    /// <see cref="ModuleStepDefinition.Retry"/>. <c>null</c> means steps without their own policy get exactly one
    /// attempt (no retries), matching pre-existing behavior.
    /// </summary>
    public RetryPolicyDefinition? Retry { get; init; }
}
