using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents a generated draft Mulse flow derived from BizTalk assets and bindings.</summary>
public sealed record BizTalkDraftFlowResponse(
    string Id,
    bool Enabled,
    BizTalkDraftTriggerResponse Trigger,
    IReadOnlyList<BizTalkDraftFetchResponse> Fetches,
    IReadOnlyList<BizTalkDraftParseResponse> Parses,
    IReadOnlyList<BizTalkDraftStepResponse> Augments,
    IReadOnlyList<BizTalkDraftDeliveryRouteResponse> Deliveries,
    IReadOnlyList<BizTalkSettingRequirementResponse> ConfigurationRequirements,
    IReadOnlyList<string> Warnings);

/// <summary>Represents one generated fetch node in a draft flow's source graph.</summary>
public sealed record BizTalkDraftFetchResponse(
    string Id,
    BizTalkDraftStepResponse Fetch,
    IReadOnlyList<string> InputParseIds);

/// <summary>Represents one generated parse node in a draft flow's source graph, parsing the listed fetch nodes' raw output.</summary>
public sealed record BizTalkDraftParseResponse(
    string Id,
    BizTalkDraftStepResponse Parse,
    IReadOnlyList<string> FetchNodeIds);

/// <summary>Represents the generated trigger settings for a draft flow.</summary>
public sealed record BizTalkDraftTriggerResponse(
    PipelineTriggerMode Mode,
    TimeSpan? Interval,
    bool RunOnStartup);

/// <summary>Represents one generated draft flow step.</summary>
public sealed record BizTalkDraftStepResponse(
    string Module,
    IReadOnlyDictionary<string, string> Settings,
    RetryPolicyDefinition? Retry = null);

/// <summary>Represents one generated draft delivery route: a single render step and its deliver actions.</summary>
public sealed record BizTalkDraftDeliveryRouteResponse(
    BizTalkDraftStepResponse Render,
    IReadOnlyList<BizTalkDraftDeliveryActionResponse> Delivers,
    string? AtomicScope = null);

/// <summary>Represents one generated deliver action of a draft delivery route, with its optional compensation step.</summary>
public sealed record BizTalkDraftDeliveryActionResponse(
    BizTalkDraftStepResponse Deliver,
    BizTalkDraftStepResponse? Compensation = null);

/// <summary>Represents a configuration or secret requirement identified during migration.</summary>
public sealed record BizTalkSettingRequirementResponse(
    string Kind,
    string SettingPath,
    string ReferenceName,
    string PlaceholderValue,
    string Description,
    bool AppliedToDraft);
