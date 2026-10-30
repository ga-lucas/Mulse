namespace Service.Models;

/// <summary>
/// Common shape shared by <see cref="CreateFlowRequest"/> and <see cref="UpdateFlowRequest"/>, which are
/// otherwise identical payloads used by different endpoints (create vs. replace). Letting both records
/// implement this interface allows shared mapping logic (see <c>FlowMappings.MapPipeline</c>) to accept
/// either request type without duplicating the mapping body per request type.
/// </summary>
public interface IFlowPipelineRequest
{
    /// <summary>The unique flow id.</summary>
    string Id { get; }

    string? Name { get; }

    string? Category { get; }

    Mulse.Modules.Pipeline.FlowMigrationReport? Migration { get; }

    /// <summary>Whether the flow is enabled.</summary>
    bool Enabled { get; }

    /// <summary>The flow trigger configuration.</summary>
    FlowTriggerRequest Trigger { get; }

    /// <summary>
    /// The fetch nodes of the flow's source graph, each optionally consuming the parsed output of parse nodes via
    /// <see cref="FlowFetchNodeRequest.InputParseIds"/>. Ids must be non-empty and unique across both fetch and
    /// parse nodes, and every fetch node must be parsed by at least one parse node.
    /// </summary>
    IReadOnlyList<FlowFetchNodeRequest> Fetches { get; }

    /// <summary>
    /// The parse nodes of the flow's source graph, each parsing the raw output of one or more fetch nodes listed in
    /// <see cref="FlowParseNodeRequest.FetchNodeIds"/>. Referenced ids must exist, and the combined fetch/parse graph
    /// must be acyclic.
    /// </summary>
    IReadOnlyList<FlowParseNodeRequest> Parses { get; }

    /// <summary>The orchestration augment steps executed after parsing.</summary>
    IReadOnlyList<FlowStepRequest> Augments { get; }

    /// <summary>The delivery routes executed after augmentation.</summary>
    IReadOnlyList<DeliveryRouteRequest> Deliveries { get; }

    /// <summary>Default retry policy applied to any step in this flow that doesn't define its own retry override.</summary>
    RetryPolicyRequest? Retry { get; }
}
