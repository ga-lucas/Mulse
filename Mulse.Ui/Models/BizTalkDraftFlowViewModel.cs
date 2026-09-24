namespace Mulse.Ui.Models;

public sealed record BizTalkDraftFlowViewModel(
    string Id,
    bool Enabled,
    BizTalkDraftTriggerViewModel Trigger,
    IReadOnlyList<BizTalkDraftFetchViewModel> Fetches,
    IReadOnlyList<BizTalkDraftParseViewModel> Parses,
    IReadOnlyList<BizTalkDraftStepViewModel> Augments,
    IReadOnlyList<BizTalkDraftDeliveryRouteViewModel> Deliveries,
    IReadOnlyList<BizTalkSettingRequirementViewModel> ConfigurationRequirements,
    IReadOnlyList<string> Warnings);

public sealed record BizTalkDraftFetchViewModel(
    string Id,
    BizTalkDraftStepViewModel Fetch,
    IReadOnlyList<string> InputParseIds);

public sealed record BizTalkDraftParseViewModel(
    string Id,
    BizTalkDraftStepViewModel Parse,
    IReadOnlyList<string> FetchNodeIds);

public sealed record BizTalkDraftTriggerViewModel(
    string Mode,
    TimeSpan? Interval,
    bool RunOnStartup);

public sealed record BizTalkDraftStepViewModel(
    string Module,
    IReadOnlyDictionary<string, string> Settings);

public sealed record BizTalkDraftDeliveryRouteViewModel(
    BizTalkDraftStepViewModel Render,
    BizTalkDraftStepViewModel Deliver);

public sealed record BizTalkSettingRequirementViewModel(
    string Kind,
    string SettingPath,
    string ReferenceName,
    string PlaceholderValue,
    string Description,
    bool AppliedToDraft);
