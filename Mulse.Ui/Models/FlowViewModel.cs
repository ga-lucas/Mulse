namespace Mulse.Ui.Models;

public sealed record FlowViewModel(
    string Id,
    bool Enabled,
    string TriggerMode,
    TimeSpan? Interval,
    bool RunOnStartup,
    IReadOnlyList<string> FetchIds,
    IReadOnlyList<string> ParseIds,
    IReadOnlyList<string> FetchModules,
    IReadOnlyList<string> ParseModules,
    IReadOnlyList<string> AugmentModules,
    IReadOnlyList<string> RenderModules,
    IReadOnlyList<string> DeliverModules,
    IReadOnlyList<FlowFetchNodeViewModel> Fetches,
    IReadOnlyList<FlowParseNodeViewModel> Parses,
    IReadOnlyList<FlowStepViewModel> Augments,
    IReadOnlyList<DeliveryRouteViewModel> Deliveries,
    RetryPolicyViewModel? Retry = null,
    string? Category = null,
    IReadOnlyList<string>? MissingModules = null,
    string? Name = null)
{
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}
