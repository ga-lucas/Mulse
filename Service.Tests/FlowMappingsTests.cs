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
            Fetches =
            [
                new FlowFetchNodeRequest
                {
                    Id = "primary-fetch",
                    Fetch = new FlowStepRequest { Module = "file-system-fetch", Settings = new Dictionary<string, string> { ["path"] = "C:\\data" } },
                    InputParseIds = []
                }
            ],
            Parses =
            [
                new FlowParseNodeRequest
                {
                    Id = "primary",
                    Parse = new FlowStepRequest { Module = "json-parse" },
                    FetchNodeIds = ["primary-fetch"]
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
        var fetch = Assert.Single(pipeline.Fetches);
        Assert.Equal("primary-fetch", fetch.Id);
        Assert.Equal("file-system-fetch", fetch.Fetch.Module);
        Assert.Equal("C:\\data", fetch.Fetch.Settings["path"]);
        Assert.Empty(fetch.InputParseIds);
        var parse = Assert.Single(pipeline.Parses);
        Assert.Equal("primary", parse.Id);
        Assert.Equal("json-parse", parse.Parse.Module);
        Assert.Equal(["primary-fetch"], parse.FetchNodeIds);
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
        var fetches = new List<FlowFetchNodeRequest>
        {
            new() { Id = "f1", Fetch = new FlowStepRequest { Module = "sftp-fetch" } }
        };
        var parses = new List<FlowParseNodeRequest>
        {
            new() { Id = "s1", Parse = new FlowStepRequest { Module = "xml-parse" }, FetchNodeIds = ["f1"] }
        };

        var createRequest = new CreateFlowRequest { Id = "flow-2", Trigger = trigger, Fetches = fetches, Parses = parses };
        var updateRequest = new UpdateFlowRequest { Id = "flow-2", Trigger = trigger, Fetches = fetches, Parses = parses };

        var fromCreate = FlowMappings.MapPipeline(createRequest);
        var fromUpdate = FlowMappings.MapPipeline(updateRequest);

        Assert.Equal(fromCreate.Id, fromUpdate.Id);
        Assert.Equal(fromCreate.Trigger.Mode, fromUpdate.Trigger.Mode);
        Assert.Equal(fromCreate.Fetches[0].Fetch.Module, fromUpdate.Fetches[0].Fetch.Module);
        Assert.Equal(fromCreate.Parses[0].Parse.Module, fromUpdate.Parses[0].Parse.Module);
        Assert.Equal(fromCreate.Parses[0].FetchNodeIds, fromUpdate.Parses[0].FetchNodeIds);
    }

    [Fact]
    public void WithEnabled_ReturnsCopyWithOnlyEnabledChanged()
    {
        var pipeline = new PipelineDefinition
        {
            Id = "flow-3",
            Enabled = true,
            Trigger = new PipelineTriggerOptions { Mode = PipelineTriggerMode.OnDemand },
            Fetches = [new FetchNodeDefinition { Id = "f1", Fetch = new ModuleStepDefinition { Module = "a" } }],
            Parses = [new ParseNodeDefinition { Id = "s1", Parse = new ModuleStepDefinition { Module = "b" }, FetchNodeIds = ["f1"] }],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "r" }, Deliver = new ModuleStepDefinition { Module = "d" } }]
        };

        var disabled = FlowMappings.WithEnabled(pipeline, enabled: false);

        Assert.False(disabled.Enabled);
        Assert.Equal(pipeline.Id, disabled.Id);
        Assert.Same(pipeline.Fetches, disabled.Fetches);
        Assert.Same(pipeline.Parses, disabled.Parses);
        Assert.Same(pipeline.Deliveries, disabled.Deliveries);
    }

    [Fact]
    public void MapFlow_RoundTripsFetchesParsesAugmentsAndDeliveries()
    {
        var pipeline = new PipelineDefinition
        {
            Id = "flow-4",
            Enabled = true,
            Trigger = new PipelineTriggerOptions { Mode = PipelineTriggerMode.Push },
            Fetches =
            [
                new FetchNodeDefinition { Id = "inbound-a", Fetch = new ModuleStepDefinition { Module = "http-inbound-fetch" } },
                new FetchNodeDefinition { Id = "inbound-b", Fetch = new ModuleStepDefinition { Module = "file-system-fetch" } },
                new FetchNodeDefinition
                {
                    Id = "lookup-fetch",
                    Fetch = new ModuleStepDefinition { Module = "sql-lookup-fetch" },
                    InputParseIds = ["primary"]
                }
            ],
            Parses =
            [
                new ParseNodeDefinition { Id = "primary", Parse = new ModuleStepDefinition { Module = "json-parse" }, FetchNodeIds = ["inbound-a", "inbound-b"] },
                new ParseNodeDefinition { Id = "lookup", Parse = new ModuleStepDefinition { Module = "json-parse" }, FetchNodeIds = ["lookup-fetch"] }
            ],
            Augments = [new ModuleStepDefinition { Module = "multi-source-join-map-augment" }],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Deliver = new ModuleStepDefinition { Module = "http-deliver" } }]
        };

        var response = FlowMappings.MapFlow(pipeline);

        Assert.Equal("flow-4", response.Id);
        Assert.Equal(["inbound-a", "inbound-b", "lookup-fetch"], response.FetchIds);
        Assert.Equal(["primary", "lookup"], response.ParseIds);
        Assert.Equal(["http-inbound-fetch", "file-system-fetch", "sql-lookup-fetch"], response.FetchModules);
        Assert.Equal(["json-parse", "json-parse"], response.ParseModules);
        Assert.Equal(["multi-source-join-map-augment"], response.AugmentModules);
        Assert.Equal(["json-render"], response.RenderModules);
        Assert.Equal(["http-deliver"], response.DeliverModules);
        Assert.Equal(3, response.Fetches.Count);
        Assert.Equal(["primary"], response.Fetches[2].InputParseIds);
        Assert.Equal(2, response.Parses.Count);
        Assert.Equal(["inbound-a", "inbound-b"], response.Parses[0].FetchNodeIds);
        Assert.Equal(["lookup-fetch"], response.Parses[1].FetchNodeIds);
    }

    [Fact]
    public void MapStep_RoundTripsOptionalNameThroughRequestAndResponse()
    {
        var request = new CreateFlowRequest
        {
            Id = "flow-named",
            Trigger = new FlowTriggerRequest { Mode = PipelineTriggerMode.OnDemand },
            Fetches =
            [
                new FlowFetchNodeRequest
                {
                    Id = "primary-fetch",
                    Fetch = new FlowStepRequest { Module = "file-system-fetch", Name = "Orders SFTP fetch" },
                    InputParseIds = []
                }
            ],
            Parses =
            [
                new FlowParseNodeRequest
                {
                    Id = "primary",
                    Parse = new FlowStepRequest { Module = "json-parse" },
                    FetchNodeIds = ["primary-fetch"]
                }
            ]
        };

        var pipeline = FlowMappings.MapPipeline(request);
        Assert.Equal("Orders SFTP fetch", pipeline.Fetches[0].Fetch.Name);
        Assert.Null(pipeline.Parses[0].Parse.Name);

        var response = FlowMappings.MapFlow(pipeline);
        Assert.Equal("Orders SFTP fetch", response.Fetches[0].Fetch.Name);
        Assert.Null(response.Parses[0].Parse.Name);
    }

    [Fact]
    public void MapPipeline_FromBizTalkDraft_MapsSplitFetchAndParseNodes()
    {
        var step = new BizTalkDraftStepResponse("file-system-fetch", new Dictionary<string, string> { ["path"] = "C:\\in" });
        var draft = new BizTalkDraftFlowResponse(
            "draft-1",
            false,
            new BizTalkDraftTriggerResponse(PipelineTriggerMode.OnDemand, null, false),
            [new BizTalkDraftFetchResponse("primary-fetch", step, [])],
            [new BizTalkDraftParseResponse("primary", new BizTalkDraftStepResponse("xml-xsd-parse", new Dictionary<string, string>()), ["primary-fetch"])],
            [],
            [new BizTalkDraftDeliveryRouteResponse(
                new BizTalkDraftStepResponse("xml-render", new Dictionary<string, string>()),
                new BizTalkDraftStepResponse("file-system-deliver", new Dictionary<string, string>()))],
            [],
            []);

        var pipeline = FlowMappings.MapPipeline(draft);

        var fetch = Assert.Single(pipeline.Fetches);
        Assert.Equal("primary-fetch", fetch.Id);
        Assert.Equal("C:\\in", fetch.Fetch.Settings["path"]);
        var parse = Assert.Single(pipeline.Parses);
        Assert.Equal("primary", parse.Id);
        Assert.Equal("xml-xsd-parse", parse.Parse.Module);
        Assert.Equal(["primary-fetch"], parse.FetchNodeIds);
    }
}
