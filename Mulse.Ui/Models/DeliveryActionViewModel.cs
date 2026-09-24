namespace Mulse.Ui.Models;

public sealed record DeliveryActionViewModel(
    FlowStepViewModel Deliver,
    FlowStepViewModel? Compensation = null);