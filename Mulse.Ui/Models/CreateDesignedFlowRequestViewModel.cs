namespace Mulse.Ui.Models;

public sealed record CreateDesignedFlowRequestViewModel(
    string Id,
    bool Enabled,
    FlowTriggerRequestViewModel Trigger,
    IReadOnlyList<FlowSourceRequestViewModel> Sources,
    IReadOnlyList<FlowStepRequestViewModel> Augments,
    IReadOnlyList<DeliveryRouteRequestViewModel> Deliveries,
    IReadOnlyList<FlowDesignFieldMappingRequestViewModel> Mappings);
