namespace Mulse.Ui.Models;

public sealed record DeliveryRouteViewModel(
    FlowStepViewModel Render,
    IReadOnlyList<DeliveryActionViewModel> Delivers,
    string? AtomicScope = null);