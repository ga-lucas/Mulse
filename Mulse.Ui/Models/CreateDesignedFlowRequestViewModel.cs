namespace Mulse.Ui.Models;

public sealed record CreateDesignedFlowRequestViewModel(
    string Id,
    bool Enabled,
    FlowTriggerRequestViewModel Trigger,
    FlowStepRequestViewModel Fetch,
    FlowStepRequestViewModel Parse,
    IReadOnlyList<FlowStepRequestViewModel> Augments,
    IReadOnlyList<DeliveryRouteRequestViewModel> Deliveries,
    IReadOnlyList<FlowDesignFieldMappingRequestViewModel> Mappings);
