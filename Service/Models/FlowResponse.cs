using Mulse.Modules;

namespace Service.Models;

/// <summary>
/// Represents a configured orchestration flow exposed by the service.
/// </summary>
/// <remarks>
/// <see cref="Trigger"/> mirrors the shape of <see cref="FlowTriggerRequest"/> (and the flattened
/// <see cref="TriggerMode"/>/<see cref="Interval"/>/<see cref="RunOnStartup"/> properties, kept for
/// backward compatibility with existing consumers) so that a client can round-trip a GET response
/// directly back into a <see cref="CreateFlowRequest"/> or <see cref="UpdateFlowRequest"/> body
/// without having to reshape the trigger separately.
/// </remarks>
public sealed record FlowResponse(
    string Id,
    bool Enabled,
    PipelineTriggerMode TriggerMode,
    TimeSpan? Interval,
    bool RunOnStartup,
    string FetchModule,
    string ParseModule,
    IReadOnlyList<string> AugmentModules,
    IReadOnlyList<string> RenderModules,
    IReadOnlyList<string> DeliverModules,
    ModuleStepResponse Fetch,
    ModuleStepResponse Parse,
    IReadOnlyList<ModuleStepResponse> Augments,
    IReadOnlyList<DeliveryRouteResponse> Deliveries,
    RetryPolicyResponse? Retry,
    FlowTriggerResponse Trigger);

/// <summary>
/// Round-trippable trigger shape matching <see cref="FlowTriggerRequest"/>. Kept as a distinct type
/// (rather than reusing the request record) so that request and response contracts can evolve
/// independently even though they happen to be identical today.
/// </summary>
public sealed record FlowTriggerResponse(
    PipelineTriggerMode Mode,
    TimeSpan? Interval,
    bool RunOnStartup);
