namespace Mulse.Ui.Models;

public sealed record DeliveryRouteRequestViewModel(
    FlowStepRequestViewModel Render,
    FlowStepRequestViewModel Deliver);
