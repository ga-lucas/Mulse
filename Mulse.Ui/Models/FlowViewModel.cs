namespace Mulse.Ui.Models;

public sealed record FlowViewModel(
    string Id,
    bool Enabled,
    string TriggerMode,
    TimeSpan? Interval,
    bool RunOnStartup,
    string FetchModule,
    string ParseModule,
    IReadOnlyList<string> AugmentModules,
    IReadOnlyList<string> RenderModules,
    IReadOnlyList<string> DeliverModules,
    FlowStepViewModel Fetch,
    FlowStepViewModel Parse,
    IReadOnlyList<FlowStepViewModel> Augments,
    IReadOnlyList<DeliveryRouteViewModel> Deliveries,
    RetryPolicyViewModel? Retry = null);
