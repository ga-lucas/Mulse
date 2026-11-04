using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Decisions;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlRuleMetadataTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static XmlRulePolicy Policy() => new()
    {
        Name = "Shared clients", Version = "1.52", RootName = "Facts", RootNamespace = "urn:facts",
        Rules = [
            new() { Name = "Client A", Conditions = [new() { Name = "GroupID", Value = "A" }], Assignments = [new() { Name = "MapName", Value = "map-A" }] },
            new() { Name = "Client B", Conditions = [new() { Name = "GroupID", Value = "B" }], Assignments = [new() { Name = "MapName", Value = "map-B" }, new() { Name = "Batch", Type = "boolean", Value = "1" }] }
        ]
    };
    private static ModuleStepDefinition Step(XmlRulePolicy? policy = null) => new() { Settings = new()
    {
        ["policyJson"] = JsonSerializer.Serialize(policy ?? Policy(), Options), ["factMode"] = "Metadata", ["metadataPrefix"] = "rules."
    } };
    private static IntegrationPayload Payload(string group) => new("order.xml", BinaryData.FromString(XmlMappingTests.Sample), "application/xml",
        new Dictionary<string, string> { ["rules.GroupID"] = group, ["requestId"] = "preserve", ["rules.Batch"] = "unchanged" });

    [Fact]
    public async Task MultipleClients_SelectDifferentRealMaps_WithoutReplacingOriginalBodies()
    {
        var first = Payload("A"); var second = Payload("B");
        var context = new FlowExecutionContext("policy-map", "test", DateTimeOffset.UtcNow);
        var selected = await new XmlRulesAugmentModule().AugmentAsync(context, new([first, second]), Step(), default);
        Assert.Same(first.Content, selected.Payloads[0].Content);
        Assert.Equal(first.ContentType, selected.Payloads[0].ContentType);
        Assert.Equal("map-A", selected.Payloads[0].Metadata["rules.MapName"]);
        Assert.Equal("unchanged", selected.Payloads[0].Metadata["rules.Batch"]);
        Assert.Equal("true", selected.Payloads[1].Metadata["rules.Batch"]);
        Assert.All(selected.Payloads, payload => Assert.Equal("preserve", payload.Metadata["requestId"]));
        Assert.False(first.Metadata.ContainsKey("rules.MapName"));
        var catalog = new FakeExecutableModuleCatalog().AddRender(new XmlMappingRenderModule());
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
            .AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>().BuildServiceProvider();
        var dispatch = new RenderDispatchModule(services.GetRequiredService<IRenderModuleInvoker>());
        var cases = new[] { "A", "B" }.Select(client => new RenderDispatchCase
        {
            Value = "map-" + client, Module = "xml-mapping-render", Settings = new()
            {
                ["mappingJson"] = XmlMappingTests.Json(new XmlMappingDefinition
                {
                    TargetRoot = "Invoice", Namespaces = new() { ["s"] = "urn:source" },
                    Fields = [new() { SourcePath = "/s:Order/s:Id", TargetPath = "Id" }, new() { TargetPath = "Client", Constant = client }]
                })
            }
        });
        var result = await dispatch.RenderAsync(context, selected, new() { Settings = new()
        {
            ["metadataKey"] = "rules.MapName", ["casesJson"] = JsonSerializer.Serialize(cases, Options)
        } }, default);
        Assert.Equal(new[] { "A", "B" }, result.Payloads.Select(payload => XDocument.Parse(payload.GetText()).Root!.Element("Client")!.Value));
        Assert.All(result.Payloads, payload => Assert.Equal("42", XDocument.Parse(payload.GetText()).Root!.Element("Id")!.Value));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("no-match")]
    [InlineData("namespace-collision")]
    [InlineData("case-collision")]
    [InlineData("conflict")]
    public async Task InvalidFactsOrPolicies_FailExplicitly_WithoutMutatingBodyOrMetadata(string scenario)
    {
        var policy = Policy();
        var payload = Payload(scenario == "no-match" ? "Unknown" : "A");
        if (scenario == "missing") payload = payload with { Metadata = new Dictionary<string, string>() };
        if (scenario == "namespace-collision") policy.Rules[0].Assignments.Add(new() { Name = "MapName", Namespace = "urn:other", Value = "other" });
        if (scenario == "case-collision") policy.Rules[0].Assignments.Add(new() { Name = "mapname", Value = "other" });
        if (scenario == "conflict") policy.Rules.Add(new() { Name = "Collision", Conditions = policy.Rules[0].Conditions, Assignments = [new() { Name = "MapName", Value = "different" }] });
        var original = payload.GetText();
        await Assert.ThrowsAsync<ArgumentException>(() => new XmlRulesAugmentModule().AugmentAsync(new("test", "test", DateTimeOffset.UtcNow), new([payload]), Step(policy), default));
        Assert.Equal(original, payload.GetText());
        Assert.False(payload.Metadata.ContainsKey("rules.MapName"));
    }

    [Fact]
    public async Task InactiveRules_DoNotRequireMetadata_AndSourceFilteringStillPassesThrough()
    {
        var policy = Policy();
        policy.Rules.Add(new() { Name = "Inactive", Active = false, Conditions = [new() { Name = "Unavailable", Value = "x" }], Assignments = [new() { Name = "Unused", Value = "x" }] });
        var step = Step(policy);
        step.Settings["sourceId"] = "orders";
        var ignored = Payload("Unknown");
        var matching = Payload("A") with { Metadata = new Dictionary<string, string> { ["sourceId"] = "orders", ["rules.GroupID"] = "A" } };
        var output = await new XmlRulesAugmentModule().AugmentAsync(new("test", "test", DateTimeOffset.UtcNow), new([ignored, matching]), step, default);
        Assert.Same(ignored, output.Payloads[0]);
        Assert.Equal("map-A", output.Payloads[1].Metadata["rules.MapName"]);
    }
}
