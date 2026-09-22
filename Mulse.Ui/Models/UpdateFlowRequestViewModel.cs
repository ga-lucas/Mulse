namespace Mulse.Ui.Models;

public sealed record UpdateFlowRequestViewModel(
    string Id,
    bool Enabled,
    FlowTriggerRequestViewModel Trigger,
    IReadOnlyList<FlowSourceRequestViewModel> Sources,
    IReadOnlyList<FlowStepRequestViewModel> Augments,
    IReadOnlyList<DeliveryRouteRequestViewModel> Deliveries,
    RetryPolicyRequestViewModel? Retry = null);
