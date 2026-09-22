namespace Mulse.Ui.Models;

public sealed record BizTalkDraftFlowViewModel(
    string Id,
    bool Enabled,
    BizTalkDraftTriggerViewModel Trigger,
    IReadOnlyList<BizTalkDraftSourceViewModel> Sources,
    IReadOnlyList<BizTalkDraftStepViewModel> Augments,
    IReadOnlyList<BizTalkDraftDeliveryRouteViewModel> Deliveries,
    IReadOnlyList<BizTalkSettingRequirementViewModel> ConfigurationRequirements,
    IReadOnlyList<string> Warnings);

public sealed record BizTalkDraftSourceViewModel(
    string Id,
    BizTalkDraftStepViewModel Fetch,
    BizTalkDraftStepViewModel Parse,
    IReadOnlyList<string> InputSourceIds);

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
