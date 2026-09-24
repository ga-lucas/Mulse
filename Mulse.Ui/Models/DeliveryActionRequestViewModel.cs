namespace Mulse.Ui.Models;

public sealed record DeliveryActionRequestViewModel(
    FlowStepRequestViewModel Deliver,
    FlowStepRequestViewModel? Compensation = null);