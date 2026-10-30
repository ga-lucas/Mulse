namespace Mulse.Ui.Models;

public sealed record CreateFlowRequestViewModel(
    string Id,
    bool Enabled,
    FlowTriggerRequestViewModel Trigger,
    IReadOnlyList<FlowFetchNodeRequestViewModel> Fetches,
    IReadOnlyList<FlowParseNodeRequestViewModel> Parses,
    IReadOnlyList<FlowStepRequestViewModel> Augments,
    IReadOnlyList<DeliveryRouteRequestViewModel> Deliveries,
    RetryPolicyRequestViewModel? Retry = null,
    string? Name = null,
    FlowMigrationReportViewModel? Migration = null);
