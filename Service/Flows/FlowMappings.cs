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
            flow.Sources.Select(static source => source.Id).ToArray(),
            flow.Sources.Select(static source => source.Fetch.Module).ToArray(),
            flow.Sources.Select(static source => source.Parse.Module).ToArray(),
            flow.Augments.Select(static step => step.Module).ToArray(),
            flow.Deliveries.Select(static route => route.Render.Module).ToArray(),
            flow.Deliveries.Select(static route => route.Deliver.Module).ToArray(),
            flow.Sources.Select(MapSource).ToArray(),
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
            Sources = request.Sources.Select(MapSource).ToList(),
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
            Sources = pipeline.Sources,
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
            Sources = draft.Sources.Select(MapSource).ToList(),
            Augments = draft.Augments.Select(MapStep).ToList(),
            Deliveries = draft.Deliveries.Select(MapDelivery).ToList()
        };
    }

    private static FlowSourceResponse MapSource(SourceDefinition source)
        => new(source.Id, MapStep(source.Fetch), MapStep(source.Parse), source.InputSourceIds.ToArray());

    private static SourceDefinition MapSource(FlowSourceRequest source)
    {
        return new SourceDefinition
        {
            Id = source.Id,
            Fetch = MapStep(source.Fetch),
            Parse = MapStep(source.Parse),
            InputSourceIds = source.InputSourceIds.ToList()
        };
    }

    private static SourceDefinition MapSource(BizTalkDraftSourceResponse source)
    {
        return new SourceDefinition
        {
            Id = source.Id,
            Fetch = MapStep(source.Fetch),
            Parse = MapStep(source.Parse),
            InputSourceIds = source.InputSourceIds.ToList()
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
