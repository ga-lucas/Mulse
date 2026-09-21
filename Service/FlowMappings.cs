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
            flow.Deliveries.Select(MapDelivery).ToArray());
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
            Deliveries = request.Deliveries.Select(MapDelivery).ToList()
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
            Deliveries = request.Deliveries.Select(MapDelivery).ToList()
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
        => new(MapStep(route.Render), MapStep(route.Deliver));

    private static DeliveryRouteDefinition MapDelivery(DeliveryRouteRequest route)
    {
        return new DeliveryRouteDefinition
        {
            Render = MapStep(route.Render),
            Deliver = MapStep(route.Deliver)
        };
    }

    private static DeliveryRouteDefinition MapDelivery(BizTalkDraftDeliveryRouteResponse route)
    {
        return new DeliveryRouteDefinition
        {
            Render = MapStep(route.Render),
            Deliver = MapStep(route.Deliver)
        };
    }

    private static ModuleStepResponse MapStep(ModuleStepDefinition step)
        => new(step.Module, new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase));

    private static ModuleStepDefinition MapStep(FlowStepRequest step)
    {
        return new ModuleStepDefinition
        {
            Module = step.Module,
            Settings = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ModuleStepDefinition MapStep(BizTalkDraftStepResponse step)
    {
        return new ModuleStepDefinition
        {
            Module = step.Module,
            Settings = new Dictionary<string, string>(step.Settings, StringComparer.OrdinalIgnoreCase)
        };
    }
}
