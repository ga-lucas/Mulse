namespace Mulse.Ui.Models;

public sealed record CreateDesignedFlowRequestViewModel(
    string Id,
    bool Enabled,
    FlowTriggerRequestViewModel Trigger,
    IReadOnlyList<FlowFetchNodeRequestViewModel> Fetches,
    IReadOnlyList<FlowParseNodeRequestViewModel> Parses,
    IReadOnlyList<FlowStepRequestViewModel> Augments,
    IReadOnlyList<DeliveryRouteRequestViewModel> Deliveries,
    IReadOnlyList<FlowDesignFieldMappingRequestViewModel> Mappings,
    string? Name = null);
