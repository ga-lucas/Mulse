namespace Mulse.Modules;

public sealed class PipelineDefinition
{
    public string Id { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public PipelineTriggerOptions Trigger { get; init; } = new();

    /// <summary>
    /// The flow's source graph. Every source runs its own fetch then parse stage; sources are executed in
    /// topological order of <see cref="SourceDefinition.InputSourceIds"/> and every source's parsed payloads are
    /// tagged with a <c>sourceId</c> metadata entry before being merged into the augment stage input.
    /// </summary>
    public List<SourceDefinition> Sources { get; init; } = [];

    public List<ModuleStepDefinition> Augments { get; init; } = [];

    public List<DeliveryRouteDefinition> Deliveries { get; init; } = [];

    /// <summary>
    /// Default retry policy applied to every step in this flow that doesn't define its own
    /// <see cref="ModuleStepDefinition.Retry"/>. <c>null</c> means steps without their own policy get exactly one
    /// attempt (no retries), matching pre-existing behavior.
    /// </summary>
    public RetryPolicyDefinition? Retry { get; init; }
}
