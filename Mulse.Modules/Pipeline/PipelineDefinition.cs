namespace Mulse.Modules.Pipeline;

public sealed class PipelineDefinition
{
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Category { get; init; }

    public bool Enabled { get; init; } = true;

    public PipelineTriggerOptions Trigger { get; init; } = new();

    /// <summary>
    /// The fetch nodes of the flow's source graph. Fetch and parse nodes are independent: each fetch node acquires
    /// raw content (optionally keyed off the parsed output of the parse nodes in
    /// <see cref="FetchNodeDefinition.InputParseIds"/>), and each parse node in <see cref="Parses"/> parses the raw
    /// output of one or more fetch nodes. Fetch and parse nodes are executed together in one topological order.
    /// </summary>
    public List<FetchNodeDefinition> Fetches { get; init; } = [];

    /// <summary>
    /// The parse nodes of the flow's source graph. Every parse node parses each of its
    /// <see cref="ParseNodeDefinition.FetchNodeIds"/> fetch nodes' raw batches with the same parse module and merges
    /// the results (many fetches to one parse); its payloads are tagged with a <c>sourceId</c> metadata entry equal
    /// to the parse node's id. Every parse node's output is merged into the augment stage input.
    /// </summary>
    public List<ParseNodeDefinition> Parses { get; init; } = [];

    public List<ModuleStepDefinition> Augments { get; init; } = [];

    public List<DeliveryRouteDefinition> Deliveries { get; init; } = [];

    /// <summary>
    /// Default retry policy applied to every step in this flow that doesn't define its own
    /// <see cref="ModuleStepDefinition.Retry"/>. <c>null</c> means steps without their own policy get exactly one
    /// attempt (no retries), matching pre-existing behavior.
    /// </summary>
    public RetryPolicyDefinition? Retry { get; init; }
}
