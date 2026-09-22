using Service.Models;

namespace Service.Tests;

public sealed class FlowDesignServiceTests
{
    private static FlowDesignService CreateService(IModuleCatalog? catalog = null, IFlowDefinitionService? flowDefinitionService = null)
        => new(
            catalog ?? FakeModuleCatalog.WithModules(("json-file-fetch", ModuleKind.Fetch), ("json-parse", ModuleKind.Parse)),
            flowDefinitionService ?? new FakeFlowDefinitionService());

    [Fact]
    public async Task AnalyzeAsync_JsonContent_DetectsJsonAndExtractsNestedFields()
    {
        var service = CreateService();
        var request = new AnalyzeFlowDesignRequest { TextContent = """{"order":{"id":"A1","total":42}}""" };

        var response = await service.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(DetectedDataFormat.Json, response.DetectedFormat);
        Assert.Contains(response.Fields, field => field.SourcePath == "order.id" && field.SampleValue == "A1");
        Assert.Contains(response.Fields, field => field.SourcePath == "order.total" && field.SampleValue == "42");
    }

    [Fact]
    public async Task AnalyzeAsync_XmlContent_DetectsXmlAndExtractsElementsAndAttributes()
    {
        var service = CreateService();
        var request = new AnalyzeFlowDesignRequest { TextContent = "<order id=\"A1\"><total>42</total></order>" };

        var response = await service.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(DetectedDataFormat.Xml, response.DetectedFormat);
        Assert.Contains(response.Fields, field => field.SourcePath == "order.@id" && field.SampleValue == "A1");
        Assert.Contains(response.Fields, field => field.SourcePath == "order.total" && field.SampleValue == "42");
    }

    [Fact]
    public async Task AnalyzeAsync_CsvContent_DetectsCsvAndUsesHeaderRowAsFieldPaths()
    {
        var service = CreateService();
        var request = new AnalyzeFlowDesignRequest { TextContent = "id,total\r\nA1,42" };

        var response = await service.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(DetectedDataFormat.Csv, response.DetectedFormat);
        Assert.Contains(response.Fields, field => field.SourcePath == "id" && field.SampleValue == "A1");
        Assert.Contains(response.Fields, field => field.SourcePath == "total" && field.SampleValue == "42");
    }

    [Fact]
    public async Task AnalyzeAsync_PlainTextContent_DetectsTextAndUsesWholeContentAsSingleField()
    {
        var service = CreateService();
        var request = new AnalyzeFlowDesignRequest { TextContent = "just some plain text" };

        var response = await service.AnalyzeAsync(request, CancellationToken.None);

        Assert.Equal(DetectedDataFormat.Text, response.DetectedFormat);
        var field = Assert.Single(response.Fields);
        Assert.Equal("content", field.SourcePath);
    }

    [Fact]
    public async Task AnalyzeAsync_BlankContent_ThrowsArgumentException()
    {
        var service = CreateService();
        var request = new AnalyzeFlowDesignRequest { TextContent = "   " };

        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeAsync_OversizedContent_ThrowsArgumentException()
    {
        var service = CreateService();
        var request = new AnalyzeFlowDesignRequest { TextContent = new string('a', 1_000_001) };

        await Assert.ThrowsAsync<ArgumentException>(() => service.AnalyzeAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task AnalyzeAsync_OnlyIncludesModulesMatchingRequestedKind()
    {
        var catalog = FakeModuleCatalog.WithModules(
            ("json-file-fetch", ModuleKind.Fetch),
            ("json-parse", ModuleKind.Parse),
            ("http-deliver", ModuleKind.Deliver));
        var service = CreateService(catalog);
        var request = new AnalyzeFlowDesignRequest { TextContent = "{}" };

        var response = await service.AnalyzeAsync(request, CancellationToken.None);

        Assert.Contains(response.FetchModules, module => module.Id == "json-file-fetch");
        Assert.DoesNotContain(response.FetchModules, module => module.Id == "json-parse");
        Assert.Contains(response.DeliverModules, module => module.Id == "http-deliver");
    }

    private static CreateDesignedFlowRequest CreateValidRequest(string flowId = "flow-1") => new()
    {
        Id = flowId,
        Enabled = true,
        Trigger = new FlowTriggerRequest { Mode = PipelineTriggerMode.OnDemand },
        Sources =
        [
            new FlowSourceRequest
            {
                Id = "primary",
                Fetch = new FlowStepRequest { Module = "fake-fetch" },
                Parse = new FlowStepRequest { Module = "fake-parse" }
            }
        ],
        Deliveries =
        [
            new DeliveryRouteRequest
            {
                Render = new FlowStepRequest { Module = "fake-render" },
                Deliver = new FlowStepRequest { Module = "fake-deliver" }
            }
        ]
    };

    [Fact]
    public async Task CreateFlowAsync_ValidRequest_BuildsPipelineAndDelegatesToFlowDefinitionService()
    {
        var flowDefinitionService = new RecordingFlowDefinitionService();
        var service = CreateService(flowDefinitionService: flowDefinitionService);

        var pipeline = await service.CreateFlowAsync(CreateValidRequest(), CancellationToken.None);

        Assert.Equal("flow-1", pipeline.Id);
        Assert.Single(pipeline.Sources);
        Assert.Single(pipeline.Deliveries);
        Assert.NotNull(flowDefinitionService.CreatedPipeline);
        Assert.Equal("flow-1", flowDefinitionService.CreatedPipeline!.Id);
    }

    [Fact]
    public async Task CreateFlowAsync_NoSources_ThrowsArgumentException()
    {
        var service = CreateService();
        var request = CreateValidRequest() with { Sources = [] };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateFlowAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateFlowAsync_NoDeliveries_ThrowsArgumentException()
    {
        var service = CreateService();
        var request = CreateValidRequest() with { Deliveries = [] };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateFlowAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateFlowAsync_MappingsWithoutJoinMapAugment_ThrowsArgumentException()
    {
        var service = CreateService();
        var request = CreateValidRequest() with
        {
            Mappings =
            [
                new FlowDesignFieldMappingRequest
                {
                    SourceKind = WorkflowFieldSourceKind.Left,
                    TargetField = "target",
                    SourcePath = "source"
                }
            ]
        };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateFlowAsync(request, CancellationToken.None));
    }

    [Fact]
    public async Task CreateFlowAsync_MappingsWithJoinMapAugment_SerializesMappingsIntoAugmentSettings()
    {
        var service = CreateService(flowDefinitionService: new RecordingFlowDefinitionService());
        var request = CreateValidRequest() with
        {
            Augments = [new FlowStepRequest { Module = "multi-source-join-map-augment" }],
            Mappings =
            [
                new FlowDesignFieldMappingRequest
                {
                    SourceKind = WorkflowFieldSourceKind.Left,
                    TargetField = "orderId",
                    SourcePath = "id"
                }
            ]
        };

        var pipeline = await service.CreateFlowAsync(request, CancellationToken.None);

        var augment = Assert.Single(pipeline.Augments);
        Assert.True(augment.Settings.ContainsKey("mappingJson"));
        Assert.Contains("orderId", augment.Settings["mappingJson"]);
    }

    private sealed class RecordingFlowDefinitionService : IFlowDefinitionService
    {
        public PipelineDefinition? CreatedPipeline { get; private set; }

        public IReadOnlyList<PipelineDefinition> GetAll() => CreatedPipeline is null ? [] : [CreatedPipeline];

        public PipelineDefinition GetById(string flowId) => CreatedPipeline ?? throw new InvalidOperationException();

        public Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken)
        {
            CreatedPipeline = pipeline;
            return Task.FromResult(pipeline);
        }

        public Task<PipelineDefinition> UpdateAsync(string flowId, PipelineDefinition pipeline, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task DeleteAsync(string flowId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
