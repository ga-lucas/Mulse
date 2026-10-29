using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules;
using Mulse.Modules.Abstractions;
using Service;

namespace Service.Tests;

public sealed class FlowModuleDependencyTests
{
    private static PipelineDefinition Pipeline() => new()
    {
        Id = "dependency-flow", Enabled = false,
        Fetches = [new FetchNodeDefinition { Id = "f", Fetch = new ModuleStepDefinition { Module = "fetch" } }],
        Parses = [new ParseNodeDefinition { Id = "p", FetchNodeIds = ["f"], Parse = new ModuleStepDefinition { Module = "parse" } }],
        Augments = [new ModuleStepDefinition
        {
            Module = "multi-source-join-map-augment",
            Settings = new() { ["mappingJson"] = "[{\"sourceKind\":\"Transform\",\"transformModule\":\"custom-transform\",\"targetField\":\"result\"}]" }
        }],
        Deliveries = [new DeliveryRouteDefinition
        {
            Render = new ModuleStepDefinition { Module = "render" },
            Delivers = [new DeliveryActionDefinition
            {
                Deliver = new ModuleStepDefinition { Module = "deliver" },
                Compensation = new ModuleStepDefinition { Module = "compensate" }
            }]
        }]
    };

    [Fact]
    public void References_IncludesEveryStageCompensationAndMappingTransforms()
    {
        var references = FlowModuleDependencies.References(Pipeline()).ToArray();
        Assert.Equal(7, references.Length);
        Assert.Contains(("compensate", ModuleKind.Deliver), references);
        Assert.Contains(("custom-transform", ModuleKind.Transform), references);
    }

    [Fact]
    public async Task EnablingRestoredFlow_RejectsMissingCompensationAndTransformUntilLoaded()
    {
        var flow = Pipeline();
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState { Pipelines = [flow] });
        var available = FakeModuleCatalog.WithModules(("fetch", ModuleKind.Fetch), ("parse", ModuleKind.Parse),
            ("multi-source-join-map-augment", ModuleKind.OrchestrationAugment), ("render", ModuleKind.Render), ("deliver", ModuleKind.Deliver));
        var service = new FlowDefinitionService(store, available);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync(flow.Id, FlowMappings.WithEnabled(flow, true), CancellationToken.None));
        Assert.Contains("compensate", error.Message);
        Assert.Contains("custom-transform", error.Message);
        Assert.False(store.GetState().Pipelines.Single().Enabled);
        var loaded = FakeModuleCatalog.WithModules(("FETCH", ModuleKind.Fetch), ("parse", ModuleKind.Parse),
            ("multi-source-join-map-augment", ModuleKind.OrchestrationAugment), ("render", ModuleKind.Render),
            ("deliver", ModuleKind.Deliver), ("compensate", ModuleKind.Deliver), ("custom-transform", ModuleKind.Transform));
        var updated = await new FlowDefinitionService(store, loaded).UpdateAsync(flow.Id, FlowMappings.WithEnabled(flow, true), CancellationToken.None);
        Assert.True(updated.Enabled);
        Assert.Empty(FlowModuleDependencies.FindMissing(updated, loaded.GetAll()));
    }

    [Fact]
    public void Availability_MatchesModuleKindAsWellAsCaseInsensitiveId()
    {
        var wrongKind = FakeModuleCatalog.WithModules(("fetch", ModuleKind.Deliver));
        Assert.Contains("Fetch: fetch", FlowModuleDependencies.FindMissing(Pipeline(), wrongKind.GetAll()));
    }

    [Fact]
    public async Task ConfigBackedMapping_UsesStoredValueAndDoesNotExposeItInErrors()
    {
        var flow = Pipeline();
        var json = flow.Augments[0].Settings["mappingJson"];
        flow.Augments[0].Settings["mappingJson"] = "{{config:mappings}}";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["mappings"] = json };
        Assert.Contains(("custom-transform", ModuleKind.Transform), FlowModuleDependencies.References(flow, values));
        var loaded = FakeModuleCatalog.WithModules(("fetch", ModuleKind.Fetch), ("parse", ModuleKind.Parse),
            ("multi-source-join-map-augment", ModuleKind.OrchestrationAugment), ("render", ModuleKind.Render),
            ("deliver", ModuleKind.Deliver), ("compensate", ModuleKind.Deliver), ("custom-transform", ModuleKind.Transform));
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState { Pipelines = [flow], ConfigValues = values });
        Assert.True((await new FlowDefinitionService(store, loaded).UpdateAsync(flow.Id, FlowMappings.WithEnabled(flow, true), CancellationToken.None)).Enabled);
        var missingConfig = FlowModuleDependencies.FindMissing(flow, loaded.GetAll());
        Assert.Contains(missingConfig, issue => issue.Contains("mappings"));
        Assert.DoesNotContain(missingConfig, issue => issue.Contains(json));
        values["mappings"] = "[null]";
        Assert.Contains(FlowModuleDependencies.FindMissing(flow, loaded.GetAll(), values), issue => issue.Contains("null mappingJson entry"));
    }

    [Fact]
    public void EmbeddedMappingTokens_ResolveDependenciesAndRejectMissingReferences()
    {
        var flow = Pipeline();
        flow.Augments[0].Settings["mappingJson"] = "[{\"sourceKind\":\"Transform\",\"transformModule\":\"{{config:prefix}}-{{secret:suffix}}\",\"targetField\":\"result\"}]";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["prefix"] = "custom", ["suffix"] = "transform" };
        Assert.Contains(("custom-transform", ModuleKind.Transform), FlowModuleDependencies.References(flow, values));
        values.Remove("suffix");
        var error = Assert.Throws<ArgumentException>(() => FlowModuleDependencies.References(flow, values).ToArray());
        Assert.Contains("suffix", error.Message);
        Assert.DoesNotContain("custom", error.Message);
    }

    [Fact]
    public async Task RunPreflight_RejectsMissingLaterStageBeforeFetchingAnything()
    {
        var fetched = false;
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new ObservedFetch(() => fetched = true))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"));
        var flow = Pipeline();
        var pipeline = new PipelineDefinition
        {
            Id = flow.Id, Enabled = true, Fetches = flow.Fetches, Parses = flow.Parses, Deliveries = flow.Deliveries
        };
        var runtime = new FlowRuntime(new FakeFlowDefinitionService([pipeline]), catalog,
            new InMemoryFlowOrchestrationStateStore(), new InMemoryFlowRetryStateStore(),
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState())), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync(flow.Id, CancellationToken.None));
        Assert.Contains("deliver", error.Message);
        Assert.Contains("compensate", error.Message);
        Assert.False(fetched);
    }

    private sealed class ObservedFetch(Action observe) : IFetchModule
    {
        public ModuleDescriptor Descriptor { get; } = new("fetch", "Observed", ModuleKind.Fetch, "Test fetch.");
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken cancellationToken)
        {
            observe();
            return Task.FromResult(IntegrationBatch.Empty);
        }
    }
}
