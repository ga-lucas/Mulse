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

    /// <summary>Whether the flow is enabled.</summary>
    bool Enabled { get; }

    /// <summary>The flow trigger configuration.</summary>
    FlowTriggerRequest Trigger { get; }

    /// <summary>
    /// The flow's source graph: each entry is a fetch/parse pair, optionally consuming other sources' parsed
    /// output. Ids must be unique and non-empty, input source ids must reference sources in this same list, and
    /// the resulting graph must be acyclic.
    /// </summary>
    IReadOnlyList<FlowSourceRequest> Sources { get; }

    /// <summary>The orchestration augment steps executed after parsing.</summary>
    IReadOnlyList<FlowStepRequest> Augments { get; }

    /// <summary>The delivery routes executed after augmentation.</summary>
    IReadOnlyList<DeliveryRouteRequest> Deliveries { get; }

    /// <summary>Default retry policy applied to any step in this flow that doesn't define its own retry override.</summary>
    RetryPolicyRequest? Retry { get; }
}
