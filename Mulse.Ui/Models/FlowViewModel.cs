namespace Mulse.Ui.Models;

public sealed record FlowViewModel(
    string Id,
    bool Enabled,
    string TriggerMode,
    TimeSpan? Interval,
    bool RunOnStartup,
    IReadOnlyList<string> SourceIds,
    IReadOnlyList<string> FetchModules,
    IReadOnlyList<string> ParseModules,
    IReadOnlyList<string> AugmentModules,
    IReadOnlyList<string> RenderModules,
    IReadOnlyList<string> DeliverModules,
    IReadOnlyList<FlowSourceViewModel> Sources,
    IReadOnlyList<FlowStepViewModel> Augments,
    IReadOnlyList<DeliveryRouteViewModel> Deliveries,
    RetryPolicyViewModel? Retry = null);
