using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Decisions;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class MapDispatchAugmentTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Runtime_NormalizesMidFlow_PromotesFacts_AppliesPolicy_MapsAgain_AndDelivers()
    {
        var original = new IntegrationPayload("order.xml", BinaryData.FromString("<Order><Id>42</Id><Group>A</Group></Order>"), "application/xml",
            new Dictionary<string, string> { ["initialMap"] = "normalize", ["requestId"] = "request-1" });
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("input", new([original])))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(new XmlMappingRenderModule())
            .AddAugment(new MetadataPromotionAugmentModule())
            .AddAugment(new XmlRulesAugmentModule())
            .AddDeliver(DelegateModuleFactory.Deliver("output", delivered));
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
            .AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>().BuildServiceProvider();
        var invoker = services.GetRequiredService<IRenderModuleInvoker>();
        var augment = new MapDispatchAugmentModule(invoker);
        catalog.AddAugment(augment).AddRender(new RenderDispatchModule(invoker));
        Assert.Equal(ModuleKind.OrchestrationAugment, augment.Descriptor.Kind);
        var normalization = new XmlMappingDefinition
        {
            TargetRoot = "Normalized", Fields = [new() { SourcePath = "/Order/Id", TargetPath = "Id" }, new() { SourcePath = "/Order/Group", TargetPath = "Group" }]
        };
        var invoice = new XmlMappingDefinition
        {
            TargetRoot = "Invoice", Fields = [new() { SourcePath = "/Normalized/Id", TargetPath = "Number" }]
        };
        var policy = new XmlRulePolicy
        {
            Name = "Client policy", Version = "1.0", RootName = "Facts", Rules = [new()
            {
                Name = "Client A", Conditions = [new() { Name = "GroupID", Value = "A" }], Assignments = [new() { Name = "MapName", Value = "invoice" }]
            }]
        };
        var flow = new PipelineDefinition
        {
            Id = "midflow", Enabled = true,
            Fetches = [new() { Id = "fetch", Fetch = new() { Module = "input" } }],
            Parses = [new() { Id = "parse", Parse = new() { Module = "parse" }, FetchNodeIds = ["fetch"] }],
            Augments = [
                new() { Module = "map-dispatch-augment", Settings = new() { ["metadataKey"] = "initialMap", ["casesJson"] = "{{config:normalization}}" } },
                new() { Module = "metadata-promotion-augment", Settings = new() { ["promotionsJson"] = "[{\"metadataKey\":\"rules.GroupID\",\"selector\":\"/Normalized/Group\"}]" } },
                new() { Module = "xml-rules-augment", Settings = new() { ["factMode"] = "Metadata", ["policyJson"] = JsonSerializer.Serialize(policy, Options) } }
            ],
            Deliveries = [new() { Render = new() { Module = "render-dispatch", Settings = new() { ["metadataKey"] = "rules.MapName", ["casesJson"] = Choices("invoice", invoice) } },
                Delivers = [new() { Deliver = new() { Module = "output" } }] }]
        };
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState { ConfigValues = new Dictionary<string, string> { ["normalization"] = Choices("normalize", normalization) } });
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, new InMemoryFlowOrchestrationStateStore(),
            new InMemoryFlowRetryStateStore(), new ConfigValueResolver(store), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
        var execution = await runtime.ExecuteAsync("midflow", default);
        Assert.Equal(FlowExecutionOutcome.Completed, execution.Outcome);
        var output = Assert.Single(Assert.Single(delivered).Payloads);
        Assert.Equal("42", XDocument.Parse(output.GetText()).Root!.Element("Number")!.Value);
        Assert.Equal("request-1", output.Metadata["requestId"]);
        Assert.Equal("A", output.Metadata["rules.GroupID"]);
        Assert.Equal("invoice", output.Metadata["rules.MapName"]);
        Assert.Equal("<Order><Id>42</Id><Group>A</Group></Order>", original.GetText());
        Assert.False(original.Metadata.ContainsKey("rules.MapName"));
    }

    private static string Choices(string value, XmlMappingDefinition mapping) => JsonSerializer.Serialize(new[]
    {
        new RenderDispatchCase { Value = value, Module = "xml-mapping-render", Settings = new() { ["mappingJson"] = XmlMappingTests.Json(mapping) } }
    }, Options);
}
