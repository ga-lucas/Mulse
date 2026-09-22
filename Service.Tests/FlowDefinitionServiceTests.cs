using Mulse.Modules;
using Service;

namespace Service.Tests;

public class FlowDefinitionServiceTests
{
    private static readonly FakeModuleCatalog ModuleCatalog = FakeModuleCatalog.WithModules(
        ("file-system-fetch", ModuleKind.Fetch),
        ("sql-lookup-fetch", ModuleKind.Fetch),
        ("json-parse", ModuleKind.Parse),
        ("json-render", ModuleKind.Render),
        ("http-deliver", ModuleKind.Deliver));

    private static PipelineDefinition ValidPipeline(string id = "flow-1") => new()
    {
        Id = id,
        Sources = [new SourceDefinition { Id = "primary", Fetch = new ModuleStepDefinition { Module = "file-system-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" } }],
        Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Deliver = new ModuleStepDefinition { Module = "http-deliver" } }]
    };

    private static FlowDefinitionService CreateService(RuntimeMulseState? state = null)
        => new(new FakeRuntimeConfigurationStore(state ?? new RuntimeMulseState()), ModuleCatalog);

    [Fact]
    public async Task CreateAsync_WithValidPipeline_Succeeds()
    {
        var service = CreateService();

        var result = await service.CreateAsync(ValidPipeline(), CancellationToken.None);

        Assert.Equal("flow-1", result.Id);
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateFlowId_Throws()
    {
        var state = new RuntimeMulseState { Pipelines = [ValidPipeline()] };
        var service = CreateService(state);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(ValidPipeline(), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithNoSources_Throws()
    {
        var service = CreateService();
        var pipeline = ValidPipeline();
        pipeline = new PipelineDefinition { Id = pipeline.Id, Sources = [], Deliveries = pipeline.Deliveries };

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithUnknownFetchModule_ThrowsInvalidOperationException()
    {
        var service = CreateService();
        var pipeline = ValidPipeline();
        pipeline.Sources[0].Fetch.GetType(); // no-op to keep record-like usage explicit
        var invalidPipeline = new PipelineDefinition
        {
            Id = pipeline.Id,
            Sources = [new SourceDefinition { Id = "primary", Fetch = new ModuleStepDefinition { Module = "does-not-exist" }, Parse = new ModuleStepDefinition { Module = "json-parse" } }],
            Deliveries = pipeline.Deliveries
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(invalidPipeline, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateSourceIds_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = new PipelineDefinition
        {
            Id = "flow-dup",
            Sources =
            [
                new SourceDefinition { Id = "primary", Fetch = new ModuleStepDefinition { Module = "file-system-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" } },
                new SourceDefinition { Id = "primary", Fetch = new ModuleStepDefinition { Module = "file-system-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" } }
            ],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Deliver = new ModuleStepDefinition { Module = "http-deliver" } }]
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("more than one source with id 'primary'", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithCyclicSourceGraph_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = new PipelineDefinition
        {
            Id = "flow-cycle",
            Sources =
            [
                new SourceDefinition { Id = "a", Fetch = new ModuleStepDefinition { Module = "file-system-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" }, InputSourceIds = ["b"] },
                new SourceDefinition { Id = "b", Fetch = new ModuleStepDefinition { Module = "sql-lookup-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" }, InputSourceIds = ["a"] }
            ],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Deliver = new ModuleStepDefinition { Module = "http-deliver" } }]
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("cyclic source graph", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithValidDiamondSourceGraph_Succeeds()
    {
        // "primary" feeds both "left" and "right", which both feed "merged" - this is acyclic (a DAG) even
        // though "merged" has two input sources, so it must not be misclassified as a cycle.
        var service = CreateService();
        var pipeline = new PipelineDefinition
        {
            Id = "flow-diamond",
            Sources =
            [
                new SourceDefinition { Id = "primary", Fetch = new ModuleStepDefinition { Module = "file-system-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" } },
                new SourceDefinition { Id = "left", Fetch = new ModuleStepDefinition { Module = "sql-lookup-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" }, InputSourceIds = ["primary"] },
                new SourceDefinition { Id = "right", Fetch = new ModuleStepDefinition { Module = "sql-lookup-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" }, InputSourceIds = ["primary"] },
                new SourceDefinition { Id = "merged", Fetch = new ModuleStepDefinition { Module = "sql-lookup-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" }, InputSourceIds = ["left", "right"] }
            ],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Deliver = new ModuleStepDefinition { Module = "http-deliver" } }]
        };

        var result = await service.CreateAsync(pipeline, CancellationToken.None);

        Assert.Equal("flow-diamond", result.Id);
    }

    [Fact]
    public async Task CreateAsync_WithUnknownInputSourceId_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = new PipelineDefinition
        {
            Id = "flow-bad-input",
            Sources = [new SourceDefinition { Id = "primary", Fetch = new ModuleStepDefinition { Module = "file-system-fetch" }, Parse = new ModuleStepDefinition { Module = "json-parse" }, InputSourceIds = ["does-not-exist"] }],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Deliver = new ModuleStepDefinition { Module = "http-deliver" } }]
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("unknown input source", exception.Message);
    }
}
