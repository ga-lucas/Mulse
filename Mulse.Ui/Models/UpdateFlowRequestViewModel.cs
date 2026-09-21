namespace Mulse.Ui.Models;

public sealed record UpdateFlowRequestViewModel(
    string Id,
    bool Enabled,
    FlowTriggerRequestViewModel Trigger,
    FlowStepRequestViewModel Fetch,
    FlowStepRequestViewModel Parse,
    IReadOnlyList<FlowStepRequestViewModel> Augments,
    IReadOnlyList<DeliveryRouteRequestViewModel> Deliveries,
    RetryPolicyRequestViewModel? Retry = null);
