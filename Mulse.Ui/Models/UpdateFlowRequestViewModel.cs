namespace Mulse.Ui.Models;

public sealed record UpdateFlowRequestViewModel(
    string Id,
    bool Enabled,
    FlowTriggerRequestViewModel Trigger,
    IReadOnlyList<FlowFetchNodeRequestViewModel> Fetches,
    IReadOnlyList<FlowParseNodeRequestViewModel> Parses,
    IReadOnlyList<FlowStepRequestViewModel> Augments,
    IReadOnlyList<DeliveryRouteRequestViewModel> Deliveries,
    RetryPolicyRequestViewModel? Retry = null,
    string? Category = null,
    string? Name = null,
    FlowMigrationReportViewModel? Migration = null);
