using Mulse.Modules;
using Service;
using Service.Models;

namespace Service.Tests;

public class FlowMappingsTests
{
    [Fact]
    public void MapPipeline_FromCreateFlowRequest_MapsAllFields()
    {
        var request = new CreateFlowRequest
        {
            Id = "flow-1",
            Enabled = true,
            Trigger = new FlowTriggerRequest { Mode = PipelineTriggerMode.Interval, Interval = TimeSpan.FromMinutes(5), RunOnStartup = true },
            Sources =
            [
                new FlowSourceRequest
                {
                    Id = "primary",
                    Fetch = new FlowStepRequest { Module = "file-system-fetch", Settings = new Dictionary<string, string> { ["path"] = "C:\\data" } },
                    Parse = new FlowStepRequest { Module = "json-parse" },
                    InputSourceIds = []
                }
            ],
            Augments = [new FlowStepRequest { Module = "decision-augment" }],
            Deliveries =
            [
                new DeliveryRouteRequest
                {
                    Render = new FlowStepRequest { Module = "json-render" },
                    Deliver = new FlowStepRequest { Module = "http-deliver" },
                    AtomicScope = "scope-1"
                }
            ],
            Retry = new RetryPolicyRequest { MaxAttempts = 3, DelaySeconds = 2, Backoff = RetryBackoffKind.Fixed }
        };

        var pipeline = FlowMappings.MapPipeline(request);

        Assert.Equal("flow-1", pipeline.Id);
        Assert.True(pipeline.Enabled);
        Assert.Equal(PipelineTriggerMode.Interval, pipeline.Trigger.Mode);
        Assert.Equal(TimeSpan.FromMinutes(5), pipeline.Trigger.Interval);
        Assert.True(pipeline.Trigger.RunOnStartup);
        Assert.Single(pipeline.Sources);
        Assert.Equal("primary", pipeline.Sources[0].Id);
        Assert.Equal("file-system-fetch", pipeline.Sources[0].Fetch.Module);
        Assert.Equal("C:\\data", pipeline.Sources[0].Fetch.Settings["path"]);
        Assert.Equal("json-parse", pipeline.Sources[0].Parse.Module);
        Assert.Single(pipeline.Augments);
        Assert.Equal("decision-augment", pipeline.Augments[0].Module);
        Assert.Single(pipeline.Deliveries);
        Assert.Equal("scope-1", pipeline.Deliveries[0].AtomicScope);
        Assert.NotNull(pipeline.Retry);
        Assert.Equal(3, pipeline.Retry!.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), pipeline.Retry.Delay);
    }

    [Fact]
    public void MapPipeline_FromUpdateFlowRequest_ProducesSameShapeAsCreateFlowRequest()
    {
        // CreateFlowRequest and UpdateFlowRequest are structurally identical and both implement
        // IFlowPipelineRequest, so FlowMappings.MapPipeline has a single implementation shared by both.
        // This test guards against that consolidation regressing if either request type diverges later.
        var trigger = new FlowTriggerRequest { Mode = PipelineTriggerMode.OnDemand, RunOnStartup = false };
        var sources = new List<FlowSourceRequest>
        {
            new()
            {
                Id = "s1",
                Fetch = new FlowStepRequest { Module = "sftp-fetch" },
                Parse = new FlowStepRequest { Module = "xml-parse" }
            }
        };

        var createRequest = new CreateFlowRequest { Id = "flow-2", Trigger = trigger, Sources = sources };
        var updateRequest = new UpdateFlowRequest { Id = "flow-2", Trigger = trigger, Sources = sources };

        var fromCreate = FlowMappings.MapPipeline(createRequest);
        var fromUpdate = FlowMappings.MapPipeline(updateRequest);

        Assert.Equal(fromCreate.Id, fromUpdate.Id);
        Assert.Equal(fromCreate.Trigger.Mode, fromUpdate.Trigger.Mode);
        Assert.Equal(fromCreate.Sources[0].Fetch.Module, fromUpdate.Sources[0].Fetch.Module);
        Assert.Equal(fromCreate.Sources[0].Parse.Module, fromUpdate.Sources[0].Parse.Module);
    }

    [Fact]
    public void WithEnabled_ReturnsCopyWithOnlyEnabledChanged()
    {
        var pipeline = new PipelineDefinition
        {
            Id = "flow-3",
            Enabled = true,
            Trigger = new PipelineTriggerOptions { Mode = PipelineTriggerMode.OnDemand },
            Sources = [new SourceDefinition { Id = "s1", Fetch = new ModuleStepDefinition { Module = "a" }, Parse = new ModuleStepDefinition { Module = "b" } }],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "r" }, Deliver = new ModuleStepDefinition { Module = "d" } }]
        };

        var disabled = FlowMappings.WithEnabled(pipeline, enabled: false);

        Assert.False(disabled.Enabled);
        Assert.Equal(pipeline.Id, disabled.Id);
        Assert.Same(pipeline.Sources, disabled.Sources);
        Assert.Same(pipeline.Deliveries, disabled.Deliveries);
    }

    [Fact]
    public void MapFlow_RoundTripsSourcesAugmentsAndDeliveries()
    {
        var pipeline = new PipelineDefinition
        {
            Id = "flow-4",
            Enabled = true,
            Trigger = new PipelineTriggerOptions { Mode = PipelineTriggerMode.Push },
            Sources =
            [
                new SourceDefinition
                {
                    Id = "primary",
                    Fetch = new ModuleStepDefinition { Module = "http-inbound-fetch" },
                    Parse = new ModuleStepDefinition { Module = "json-parse" }
                },
                new SourceDefinition
                {
                    Id = "lookup",
                    Fetch = new ModuleStepDefinition { Module = "sql-lookup-fetch" },
                    Parse = new ModuleStepDefinition { Module = "json-parse" },
                    InputSourceIds = ["primary"]
                }
            ],
            Augments = [new ModuleStepDefinition { Module = "multi-source-join-map-augment" }],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Deliver = new ModuleStepDefinition { Module = "http-deliver" } }]
        };

        var response = FlowMappings.MapFlow(pipeline);

        Assert.Equal("flow-4", response.Id);
        Assert.Equal(["primary", "lookup"], response.SourceIds);
        Assert.Equal(["http-inbound-fetch", "sql-lookup-fetch"], response.FetchModules);
        Assert.Equal(["json-parse", "json-parse"], response.ParseModules);
        Assert.Equal(["multi-source-join-map-augment"], response.AugmentModules);
        Assert.Equal(["json-render"], response.RenderModules);
        Assert.Equal(["http-deliver"], response.DeliverModules);
        Assert.Equal(2, response.Sources.Count);
        Assert.Equal(["primary"], response.Sources[1].InputSourceIds);
    }
}
