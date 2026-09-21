using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents a configured orchestration flow exposed by the service.</summary>
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
    IReadOnlyList<DeliveryRouteResponse> Deliveries);
