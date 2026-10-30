using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class XmlMappingTests
{
    internal const string Sample = "<Order xmlns='urn:source' id='A&amp;B'><Id>42</Id><Empty /></Order>";
    internal static readonly XmlMappingDefinition Map = new()
    {
        TargetRoot = "t:Result",
        Namespaces = new() { ["s"] = "urn:source", ["t"] = "urn:target" },
        Fields =
        [
            new() { SourcePath = "/s:Order/s:Id", TargetPath = "t:Header/t:Number" },
            new() { SourcePath = "/s:Order/@id", TargetPath = "@id" },
            new() { SourcePath = "/s:Order/s:Empty", TargetPath = "Empty" },
            new() { TargetPath = "t:Literal", Constant = "<&>{'quoted'}" },
            new() { TargetPath = "t:Blank", Constant = "" }
        ]
    };
    internal static string Json(XmlMappingDefinition? map = null) => JsonSerializer.Serialize(map ?? Map, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void ScalarMap_PreservesExpandedNamesAttributesEmptyValuesAndEscapedConstants()
    {
        var engine = new XmlMappingEngine(Json());
        var output = XDocument.Parse(engine.Transform(Sample));
        XNamespace target = "urn:target";
        Assert.Equal(target + "Result", output.Root!.Name);
        Assert.Equal("A&B", output.Root.Attribute("id")!.Value);
        Assert.Equal("42", output.Root.Element(target + "Header")!.Element(target + "Number")!.Value);
        Assert.Equal("", output.Root.Element("Empty")!.Value);
        Assert.Equal("<&>{'quoted'}", output.Root.Element(target + "Literal")!.Value);
        Assert.Equal("", output.Root.Element(target + "Blank")!.Value);
        Assert.DoesNotContain("document(", engine.Xslt);
        Assert.DoesNotContain("script", engine.Xslt);
    }

    [Theory]
    [InlineData("<Order xmlns='urn:source' id='x'><Empty /></Order>")]
    [InlineData("<Order xmlns='urn:source' id='x'><Id>1</Id><Id>2</Id><Empty /></Order>")]
    [InlineData("<Order xmlns='urn:source' id='x'><Id><Child>1</Child></Id><Empty /></Order>")]
    [InlineData("<Order xmlns='urn:wrong' id='x'><Id>1</Id><Empty /></Order>")]
    public void MissingRepeatedComplexOrWrongNamespaceSources_AreRejected(string input)
    {
        var exception = Assert.Throws<ArgumentException>(() => new XmlMappingEngine(Json()).Transform(input));
        Assert.Contains("exactly one scalar", exception.Message);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"fields\":null}")]
    [InlineData("{\"namespaces\":null}")]
    [InlineData("{\"fields\":[null]}")]
    [InlineData("{\"fields\":[]}")]
    [InlineData("{\"unsupportedSetting\":true}")]
    public void InvalidOrFutureDefinitions_AreRejected(string json)
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(json));

    [Theory]
    [InlineData("/Order/*", "Id")]
    [InlineData("/Order/Id[1]", "Id")]
    [InlineData("document('file:///private.xml')", "Id")]
    [InlineData("//Id", "Id")]
    [InlineData("/Order/../Id", "Id")]
    [InlineData("/Order/unknown:Id", "Id")]
    [InlineData("/Order/@id/Value", "Id")]
    [InlineData("/Order/Id", "/Output/Id")]
    [InlineData("/Order/Id", "@id/Value")]
    public void UnsupportedPaths_AreRejectedRatherThanInterpretedAsXPath(string source, string target)
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(Json(Map with
        {
            Fields = [new() { SourcePath = source, TargetPath = target }]
        })));

    [Theory]
    [InlineData("Same", "Same")]
    [InlineData("Container", "Container/Child")]
    [InlineData("Container/Child", "Container")]
    [InlineData("t:Same", "alias:Same")]
    public void DuplicateOrOverlappingTargets_AreRejected(string first, string second)
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(Json(Map with
        {
            Namespaces = new() { ["t"] = "urn:target", ["alias"] = "urn:target" },
            Fields = [new() { TargetPath = first, Constant = "1" }, new() { TargetPath = second, Constant = "2" }]
        })));

    [Theory]
    [InlineData("xsl", "urn:custom")]
    [InlineData("xml", "http://www.w3.org/XML/1998/namespace")]
    [InlineData("", "urn:source")]
    [InlineData("s", "")]
    [InlineData("s", "relative")]
    public void InvalidNamespaces_AreRejected(string prefix, string uri)
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(Json(Map with { Namespaces = new() { [prefix] = uri } })));

    [Fact]
    public void AmbiguousValueKind_IsRejected()
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(Json(Map with
        {
            Fields = [new() { SourcePath = "/Order/Id", TargetPath = "Id", Constant = "not ignored" }]
        })));

    [Theory]
    [InlineData("<!DOCTYPE root [<!ENTITY x SYSTEM 'file:///not-read'>]><root>&x;</root>")]
    [InlineData("<broken>")]
    public void UnsafeOrMalformedXml_IsRejected(string xml)
        => Assert.Throws<ArgumentException>(() => new XmlMappingEngine(Json()).Transform(xml));

    [Fact]
    public void LimitsAndCancellation_AreEnforced()
    {
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(new string(' ', 262_145)));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(Json()).Transform(new string('x', 1_000_001)));
        Assert.Throws<ArgumentException>(() => new XmlMappingEngine(Json(Map with { Fields = Enumerable.Repeat(Map.Fields[0], 129).ToArray() })));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => new XmlMappingEngine(Json()).Transform(Sample, cancelled.Token));
        var amplifier = new XmlMappingEngine(Json(Map with
        {
            TargetRoot = "Output", Namespaces = new(),
            Fields = Enumerable.Range(0, 10).Select(index => new XmlFieldMappingDefinition { SourcePath = "/Input/Value", TargetPath = "Field" + index }).ToArray()
        }));
        Assert.Throws<ArgumentException>(() => amplifier.Transform("<Input><Value>" + new string('x', 500_000) + "</Value></Input>"));
    }

    [Fact]
    public async Task SavedMapping_ReloadsDuplicatesAndExecutesThroughTheRealRenderModule()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-xml-mapping-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var delivered = new List<IntegrationBatch>();
            var input = new IntegrationBatch([new IntegrationPayload("order.txt", BinaryData.FromString(Sample), "application/xml", new Dictionary<string, string> { ["trace"] = "source" })]);
            var catalog = new FakeExecutableModuleCatalog()
                .AddFetch(DelegateModuleFactory.Fetch("fetch", input))
                .AddParse(new XmlXsdParseModule()).AddRender(new XmlMappingRenderModule())
                .AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
            RuntimeConfigurationStore Store() => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }),
                new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
            var definitions = new FlowDefinitionService(Store(), catalog);
            await definitions.CreateAsync(Pipeline(), default);
            var store = Store();
            definitions = new FlowDefinitionService(store, catalog);
            var restored = definitions.GetById("xml-map");
            Assert.Equal(Json(), restored.Deliveries[0].Render.Settings["mappingJson"]);
            var copy = await definitions.DuplicateAsync("xml-map", "xml-map-copy", default);
            Assert.Equal(Json(), copy.Deliveries[0].Render.Settings["mappingJson"]);
            var runtime = new FlowRuntime(definitions, catalog, new InMemoryFlowOrchestrationStateStore(), new InMemoryFlowRetryStateStore(),
                new ConfigValueResolver(store), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
            Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ExecuteAsync("xml-map", default)).Outcome);
            var output = Assert.Single(Assert.Single(delivered).Payloads);
            Assert.Equal("order.xml", output.Name);
            Assert.Equal("application/xml", output.ContentType);
            Assert.Equal("source", output.Metadata["trace"]);
            Assert.Equal("xml-mapping-render", output.Metadata["renderedBy"]);
            Assert.Equal(new XmlMappingEngine(Json()).Transform(Sample), output.GetText());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static PipelineDefinition Pipeline() => new()
    {
        Id = "xml-map", Enabled = true,
        Fetches = [new() { Id = "fetch", Fetch = new() { Module = "fetch" } }],
        Parses = [new() { Id = "parsed", FetchNodeIds = ["fetch"], Parse = new() { Module = "xml-xsd-parse" } }],
        Deliveries = [new() { Render = new() { Module = "xml-mapping-render", Settings = new() { ["mappingJson"] = Json() } }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }]
    };
}
