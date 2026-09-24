namespace Mulse.Ui.Models;

public sealed record DeliveryRouteRequestViewModel(
    FlowStepRequestViewModel Render,
    IReadOnlyList<DeliveryActionRequestViewModel> Delivers,
    string? AtomicScope = null);