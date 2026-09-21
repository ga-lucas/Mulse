using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents a generated draft Mulse flow derived from BizTalk assets and bindings.</summary>
public sealed record BizTalkDraftFlowResponse(
    string Id,
    bool Enabled,
    BizTalkDraftTriggerResponse Trigger,
    BizTalkDraftStepResponse Fetch,
    BizTalkDraftStepResponse Parse,
    IReadOnlyList<BizTalkDraftStepResponse> Augments,
    IReadOnlyList<BizTalkDraftDeliveryRouteResponse> Deliveries,
    IReadOnlyList<BizTalkSettingRequirementResponse> ConfigurationRequirements,
    IReadOnlyList<string> Warnings);

/// <summary>Represents the generated trigger settings for a draft flow.</summary>
public sealed record BizTalkDraftTriggerResponse(
    PipelineTriggerMode Mode,
    TimeSpan? Interval,
    bool RunOnStartup);

/// <summary>Represents one generated draft flow step.</summary>
public sealed record BizTalkDraftStepResponse(
    string Module,
    IReadOnlyDictionary<string, string> Settings);

/// <summary>Represents one generated draft delivery route.</summary>
public sealed record BizTalkDraftDeliveryRouteResponse(
    BizTalkDraftStepResponse Render,
    BizTalkDraftStepResponse Deliver);

/// <summary>Represents a configuration or secret requirement identified during migration.</summary>
public sealed record BizTalkSettingRequirementResponse(
    string Kind,
    string SettingPath,
    string ReferenceName,
    string PlaceholderValue,
    string Description,
    bool AppliedToDraft);
