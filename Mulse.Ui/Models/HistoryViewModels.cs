namespace Mulse.Ui.Models;

public sealed record HistoryExecutionViewModel(string Id, string FlowId, string State, long StartedUtcTicks, long UpdatedUtcTicks);

public sealed record HistoryExecutionDetailViewModel(
    HistoryExecutionViewModel Execution,
    IReadOnlyList<HistoryEventViewModel> Events,
    IReadOnlyList<HistoryMessageViewModel> Messages,
    IReadOnlyList<HistoryDeliveryViewModel> Deliveries);

public sealed record HistoryEventViewModel(Guid Id, string Stage, string State, long CreatedUtcTicks, string? Detail);

public sealed record HistoryMessageViewModel(Guid Id, string Channel, string State, string? ExecutionId, long CreatedUtcTicks);

public sealed record HistoryDeliveryViewModel(Guid Id, string Stage, string State, long UpdatedUtcTicks);

public sealed record HistoryMessagePayloadViewModel(string Json);
