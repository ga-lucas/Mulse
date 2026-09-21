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
}
