using Mulse.Modules;
using Service.Models;

namespace Service;

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
            flow.Fetch.Module,
            flow.Parse.Module,
            flow.Augments.Select(static step => step.Module).ToArray(),
            flow.Deliveries.Select(static route => route.Render.Module).ToArray(),
            flow.Deliveries.Select(static route => route.Deliver.Module).ToArray(),
            MapStep(flow.Fetch),
            MapStep(flow.Parse),
            flow.Augments.Select(MapStep).ToArray(),
            flow.Deliveries.Select(MapDelivery).ToArray(),
            MapRetry(flow.Retry),
            new FlowTriggerResponse(flow.Trigger.Mode, flow.Trigger.Interval, flow.Trigger.RunOnStartup));
    }

    public static PipelineDefinition MapPipeline(CreateFlowRequest request)
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
            Fetch = MapStep(request.Fetch),
            Parse = MapStep(request.Parse),
            Augments = request.Augments.Select(MapStep).ToList(),
            Deliveries = request.Deliveries.Select(MapDelivery).ToList(),
            Retry = MapRetry(request.Retry)
        };
    }

    public static PipelineDefinition MapPipeline(UpdateFlowRequest request)
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
            Fetch = MapStep(request.Fetch),
            Parse = MapStep(request.Parse),
            Augments = request.Augments.Select(MapStep).ToList(),
            Deliveries = request.Deliveries.Select(MapDelivery).ToList(),
            Retry = MapRetry(request.Retry)
        };
    }

    /// <summary>
    /// Returns a copy of <paramref name="pipeline"/> with only <see cref="PipelineDefinition.Enabled"/>
    /// changed. Used by the dedicated enable/disable endpoint so callers can toggle a flow without
    /// resending its entire fetch/parse/augment/delivery definition.
    /// </summary>
    public static PipelineDefinition WithEnabled(PipelineDefinition pipeline, bool enabled)
    {
        return new PipelineDefinition
        {
            Id = pipeline.Id,
            Enabled = enabled,
            Trigger = pipeline.Trigger,
            Fetch = pipeline.Fetch,
            Parse = pipeline.Parse,
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
            Fetch = MapStep(draft.Fetch),
            Parse = MapStep(draft.Parse),
            Augments = draft.Augments.Select(MapStep).ToList(),
            Deliveries = draft.Deliveries.Select(MapDelivery).ToList()
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
        => new(step.Module, new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase), MapRetry(step.Retry));

    private static ModuleStepDefinition MapStep(FlowStepRequest step)
    {
        return new ModuleStepDefinition
        {
            Module = step.Module,
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
