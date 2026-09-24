using Mulse.Modules;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Pipeline;
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

    private static readonly List<DeliveryRouteDefinition> Deliveries =
        [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "http-deliver" } }] }];

    private static FetchNodeDefinition Fetch(string id, string module = "file-system-fetch", params string[] inputParseIds)
        => new() { Id = id, Fetch = new ModuleStepDefinition { Module = module }, InputParseIds = [.. inputParseIds] };

    private static ParseNodeDefinition Parse(string id, params string[] fetchNodeIds)
        => new() { Id = id, Parse = new ModuleStepDefinition { Module = "json-parse" }, FetchNodeIds = [.. fetchNodeIds] };

    private static PipelineDefinition Pipeline(string id, List<FetchNodeDefinition> fetches, List<ParseNodeDefinition> parses)
        => new() { Id = id, Fetches = fetches, Parses = parses, Deliveries = Deliveries };

    private static PipelineDefinition PipelineWithDeliveries(string id, List<DeliveryRouteDefinition> deliveries)
        => new() { Id = id, Fetches = [Fetch("primary-fetch")], Parses = [Parse("primary", "primary-fetch")], Deliveries = deliveries };

    private static PipelineDefinition ValidPipeline(string id = "flow-1")
        => Pipeline(id, [Fetch("primary-fetch")], [Parse("primary", "primary-fetch")]);

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
    public async Task CreateAsync_WithEmptySourceGraph_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Pipeline("flow-empty", [], []), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithFetchesButNoParses_Throws()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(Pipeline("flow-no-parse", [Fetch("primary-fetch")], []), CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithUnknownFetchModule_ThrowsInvalidOperationException()
    {
        var service = CreateService();
        var invalidPipeline = Pipeline("flow-1", [Fetch("primary-fetch", "does-not-exist")], [Parse("primary", "primary-fetch")]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(invalidPipeline, CancellationToken.None));
    }

    [Fact]
    public async Task CreateAsync_WithMissingParseModule_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = Pipeline(
            "flow-missing-module",
            [Fetch("primary-fetch")],
            [new ParseNodeDefinition { Id = "primary", Parse = new ModuleStepDefinition { Module = "" }, FetchNodeIds = ["primary-fetch"] }]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("parse node 'primary' must define a parse module", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithMissingFetchModule_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = Pipeline("flow-missing-module", [Fetch("primary-fetch", " ")], [Parse("primary", "primary-fetch")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("fetch node 'primary-fetch' must define a fetch module", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateFetchIds_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = Pipeline("flow-dup", [Fetch("primary-fetch"), Fetch("primary-fetch")], [Parse("primary", "primary-fetch")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("more than one fetch node with id 'primary-fetch'", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithDuplicateParseIds_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = Pipeline("flow-dup", [Fetch("primary-fetch")], [Parse("primary", "primary-fetch"), Parse("primary", "primary-fetch")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("more than one parse node with id 'primary'", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithFetchAndParseSharingAnId_ThrowsArgumentException()
    {
        // Fetch and parse ids share one namespace, so the combined graph can reference either unambiguously.
        var service = CreateService();
        var pipeline = Pipeline("flow-shared-id", [Fetch("primary")], [Parse("primary", "primary")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("uses id 'primary' for both a fetch node and a parse node", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithCyclicSourceGraph_ThrowsArgumentException()
    {
        // a-fetch -> a -> b-fetch -> b -> a-fetch (each fetch consumes the other pair's parsed output).
        var service = CreateService();
        var pipeline = Pipeline(
            "flow-cycle",
            [Fetch("a-fetch", "file-system-fetch", "b"), Fetch("b-fetch", "sql-lookup-fetch", "a")],
            [Parse("a", "a-fetch"), Parse("b", "b-fetch")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("cyclic source graph", exception.Message);
        Assert.Contains("a-fetch", exception.Message);
        Assert.Contains("b-fetch", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithValidDiamondSourceGraph_Succeeds()
    {
        // "primary" feeds both "left" and "right" lookups, which both feed "merged" - this is acyclic (a DAG) even
        // though "merged-fetch" has two input parse nodes, so it must not be misclassified as a cycle.
        var service = CreateService();
        var pipeline = Pipeline(
            "flow-diamond",
            [
                Fetch("primary-fetch"),
                Fetch("left-fetch", "sql-lookup-fetch", "primary"),
                Fetch("right-fetch", "sql-lookup-fetch", "primary"),
                Fetch("merged-fetch", "sql-lookup-fetch", "left", "right")
            ],
            [Parse("primary", "primary-fetch"), Parse("left", "left-fetch"), Parse("right", "right-fetch"), Parse("merged", "merged-fetch")]);

        var result = await service.CreateAsync(pipeline, CancellationToken.None);

        Assert.Equal("flow-diamond", result.Id);
    }

    [Fact]
    public async Task CreateAsync_WithSeveralFetchesFeedingOneParse_Succeeds()
    {
        var service = CreateService();
        var pipeline = Pipeline(
            "flow-many-to-one",
            [Fetch("inbox-a"), Fetch("inbox-b"), Fetch("inbox-c")],
            [Parse("orders", "inbox-a", "inbox-b", "inbox-c")]);

        var result = await service.CreateAsync(pipeline, CancellationToken.None);

        Assert.Equal(["inbox-a", "inbox-b", "inbox-c"], Assert.Single(result.Parses).FetchNodeIds);
    }

    [Fact]
    public async Task CreateAsync_WithUnknownInputParseId_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = Pipeline("flow-bad-input", [Fetch("primary-fetch", "file-system-fetch", "does-not-exist")], [Parse("primary", "primary-fetch")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("references unknown input parse node 'does-not-exist'", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithInputParseIdNamingAFetchNode_ThrowsArgumentException()
    {
        // InputParseIds must name parse nodes: a fetch consumes finished (parsed) batches, never raw ones.
        var service = CreateService();
        var pipeline = Pipeline(
            "flow-bad-input-kind",
            [Fetch("primary-fetch"), Fetch("lookup-fetch", "sql-lookup-fetch", "primary-fetch")],
            [Parse("primary", "primary-fetch"), Parse("lookup", "lookup-fetch")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("references unknown input parse node 'primary-fetch'", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithUnknownFetchNodeId_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = Pipeline("flow-bad-fetch-ref", [Fetch("primary-fetch")], [Parse("primary", "primary-fetch", "missing-fetch")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("parse node 'primary' references unknown fetch node 'missing-fetch'", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithParseNodeWithoutFetchNodes_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = Pipeline("flow-empty-parse", [Fetch("primary-fetch")], [Parse("primary", "primary-fetch"), Parse("orphan")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("parse node 'orphan' must reference at least one fetch node", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithFetchNodeNotParsedByAnyParseNode_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = Pipeline("flow-orphan-fetch", [Fetch("primary-fetch"), Fetch("unused-fetch")], [Parse("primary", "primary-fetch")]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("fetch node 'unused-fetch' is not parsed by any parse node", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithDeliveryRouteWithoutDelivers_ThrowsArgumentException()
    {
        var service = CreateService();
        var pipeline = PipelineWithDeliveries("flow-no-delivers",
            [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "json-render" }, Delivers = [] }]);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(pipeline, CancellationToken.None));
        Assert.Contains("delivery route 0 must define at least one deliver action", exception.Message);
    }

    [Fact]
    public async Task CreateAsync_WithMultipleDeliversPerRoute_Succeeds()
    {
        var service = CreateService();
        var pipeline = PipelineWithDeliveries("flow-fan-out",
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "json-render" },
                    Delivers =
                    [
                        new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "http-deliver" } },
                        new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "http-deliver" } }
                    ]
                }
            ]);

        var result = await service.CreateAsync(pipeline, CancellationToken.None);

        Assert.Equal(2, Assert.Single(result.Deliveries).Delivers.Count);
    }

    [Fact]
    public async Task CreateAsync_WithUnknownModuleInSecondDeliver_ThrowsInvalidOperationException()
    {
        var service = CreateService();
        var pipeline = PipelineWithDeliveries("flow-bad-second-deliver",
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "json-render" },
                    Delivers =
                    [
                        new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "http-deliver" } },
                        new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "missing-deliver" } }
                    ]
                }
            ]);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync(pipeline, CancellationToken.None));
    }
}
