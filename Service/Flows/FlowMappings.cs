using Mulse.Modules;
using Service.Models;

namespace Service.Flows;

internal static class FlowMappings
{
    public static FlowResponse MapFlow(PipelineDefinition flow)
    {
        return new FlowResponse(
            flow.Id,
            flow.Enabled,
            flow.Trigger.Mode,
            flow.Trigger.Interval,
            flow.Trigger.RunOnStartup,
            flow.Fetches.Select(static fetch => fetch.Id).ToArray(),
            flow.Parses.Select(static parse => parse.Id).ToArray(),
            flow.Fetches.Select(static fetch => fetch.Fetch.Module).ToArray(),
            flow.Parses.Select(static parse => parse.Parse.Module).ToArray(),
            flow.Augments.Select(static step => step.Module).ToArray(),
            flow.Deliveries.Select(static route => route.Render.Module).ToArray(),
            flow.Deliveries.Select(static route => route.Deliver.Module).ToArray(),
            flow.Fetches.Select(MapFetchNode).ToArray(),
            flow.Parses.Select(MapParseNode).ToArray(),
            flow.Augments.Select(MapStep).ToArray(),
            flow.Deliveries.Select(MapDelivery).ToArray(),
            MapRetry(flow.Retry),
            new FlowTriggerResponse(flow.Trigger.Mode, flow.Trigger.Interval, flow.Trigger.RunOnStartup));
    }

    /// <summary>
    /// Maps either a <see cref="CreateFlowRequest"/> or <see cref="UpdateFlowRequest"/> (both implement
    /// <see cref="IFlowPipelineRequest"/> and are otherwise structurally identical) to a runtime
    /// <see cref="PipelineDefinition"/>.
    /// </summary>
    public static PipelineDefinition MapPipeline(IFlowPipelineRequest request)
    {
        return new PipelineDefinition
        {
            Id = request.Id,
            Enabled = request.Enabled,
            Trigger = new PipelineTriggerOptions
            {
                Mode = request.Trigger.Mode,
                Interval = request.Trigger.Interval,
                RunOnStartup = request.Trigger.RunOnStartup
            },
            Fetches = request.Fetches.Select(MapFetchNode).ToList(),
            Parses = request.Parses.Select(MapParseNode).ToList(),
            Augments = request.Augments.Select(MapStep).ToList(),
            Deliveries = request.Deliveries.Select(MapDelivery).ToList(),
            Retry = MapRetry(request.Retry)
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="pipeline"/> with only <see cref="PipelineDefinition.Enabled"/>
    /// changed. Used by the dedicated enable/disable endpoint so callers can toggle a flow without
    /// resending its entire source/augment/delivery definition.
    /// </summary>
    public static PipelineDefinition WithEnabled(PipelineDefinition pipeline, bool enabled)
    {
        return new PipelineDefinition
        {
            Id = pipeline.Id,
            Enabled = enabled,
            Trigger = pipeline.Trigger,
            Fetches = pipeline.Fetches,
            Parses = pipeline.Parses,
            Augments = pipeline.Augments,
            Deliveries = pipeline.Deliveries,
            Retry = pipeline.Retry
        };
    }

    public static PipelineDefinition MapPipeline(BizTalkDraftFlowResponse draft)
    {
        return new PipelineDefinition
        {
            Id = draft.Id,
            Enabled = draft.Enabled,
            Trigger = new PipelineTriggerOptions
            {
                Mode = draft.Trigger.Mode,
                Interval = draft.Trigger.Interval,
                RunOnStartup = draft.Trigger.RunOnStartup
            },
            Fetches = draft.Fetches.Select(MapFetchNode).ToList(),
            Parses = draft.Parses.Select(MapParseNode).ToList(),
            Augments = draft.Augments.Select(MapStep).ToList(),
            Deliveries = draft.Deliveries.Select(MapDelivery).ToList()
        };
    }

    private static FlowFetchNodeResponse MapFetchNode(FetchNodeDefinition fetch)
        => new(fetch.Id, MapStep(fetch.Fetch), fetch.InputParseIds.ToArray());

    private static FlowParseNodeResponse MapParseNode(ParseNodeDefinition parse)
        => new(parse.Id, MapStep(parse.Parse), parse.FetchNodeIds.ToArray());

    /// <summary>Maps an API fetch node request to its runtime definition. Shared with <see cref="FlowDesignService"/>.</summary>
    public static FetchNodeDefinition MapFetchNode(FlowFetchNodeRequest fetch)
    {
        return new FetchNodeDefinition
        {
            Id = fetch.Id,
            Fetch = MapStep(fetch.Fetch),
            InputParseIds = (fetch.InputParseIds ?? []).ToList()
        };
    }

    /// <summary>Maps an API parse node request to its runtime definition. Shared with <see cref="FlowDesignService"/>.</summary>
    public static ParseNodeDefinition MapParseNode(FlowParseNodeRequest parse)
    {
        return new ParseNodeDefinition
        {
            Id = parse.Id,
            Parse = MapStep(parse.Parse),
            FetchNodeIds = (parse.FetchNodeIds ?? []).ToList()
        };
    }

    private static FetchNodeDefinition MapFetchNode(BizTalkDraftFetchResponse fetch)
    {
        return new FetchNodeDefinition
        {
            Id = fetch.Id,
            Fetch = MapStep(fetch.Fetch),
            InputParseIds = fetch.InputParseIds.ToList()
        };
    }

    private static ParseNodeDefinition MapParseNode(BizTalkDraftParseResponse parse)
    {
        return new ParseNodeDefinition
        {
            Id = parse.Id,
            Parse = MapStep(parse.Parse),
            FetchNodeIds = parse.FetchNodeIds.ToList()
        };
    }

    private static DeliveryRouteResponse MapDelivery(DeliveryRouteDefinition route)
        => new(MapStep(route.Render), MapStep(route.Deliver), route.AtomicScope, route.Compensation is null ? null : MapStep(route.Compensation));

    private static DeliveryRouteDefinition MapDelivery(DeliveryRouteRequest route)
    {
        return new DeliveryRouteDefinition
        {
            Render = MapStep(route.Render),
            Deliver = MapStep(route.Deliver),
            AtomicScope = route.AtomicScope,
            Compensation = route.Compensation is null ? null : MapStep(route.Compensation)
        };
    }

    private static DeliveryRouteDefinition MapDelivery(BizTalkDraftDeliveryRouteResponse route)
    {
        return new DeliveryRouteDefinition
        {
            Render = MapStep(route.Render),
            Deliver = MapStep(route.Deliver),
            AtomicScope = route.AtomicScope,
            Compensation = route.Compensation is null ? null : MapStep(route.Compensation)
        };
    }

    private static ModuleStepResponse MapStep(ModuleStepDefinition step)
        => new(step.Module, new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase), MapRetry(step.Retry), step.Name);

    private static ModuleStepDefinition MapStep(FlowStepRequest step)
    {
        return new ModuleStepDefinition
        {
            Module = step.Module,
            Name = step.Name,
            Settings = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase),
            Retry = MapRetry(step.Retry)
        };
    }

    private static ModuleStepDefinition MapStep(BizTalkDraftStepResponse step)
    {
        return new ModuleStepDefinition
        {
            Module = step.Module,
            Settings = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase),
            Retry = step.Retry
        };
    }

    private static RetryPolicyResponse? MapRetry(RetryPolicyDefinition? retry)
        => retry is null ? null : new RetryPolicyResponse(retry.MaxAttempts, retry.Delay.TotalSeconds, retry.Backoff, retry.MaxDelay?.TotalSeconds);

    private static RetryPolicyDefinition? MapRetry(RetryPolicyRequest? retry)
        => retry is null ? null : new RetryPolicyDefinition
        {
            MaxAttempts = retry.MaxAttempts,
            Delay = TimeSpan.FromSeconds(retry.DelaySeconds),
            Backoff = retry.Backoff,
            MaxDelay = retry.MaxDelaySeconds is { } maxDelaySeconds ? TimeSpan.FromSeconds(maxDelaySeconds) : null
        };
}
