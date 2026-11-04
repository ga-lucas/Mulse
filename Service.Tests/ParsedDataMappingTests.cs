using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mulse.Excel;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.BuiltIn.Transform;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class ParsedDataMappingTests
{
    internal static string MapJson(params ParsedDataFieldMap[] fields) => JsonSerializer.Serialize(new ParsedDataMap { Fields = fields.ToList() }, ParsedDataMapping.JsonOptions);
    internal static FlowExecutionContext Context => new("mapping", "test", DateTimeOffset.UtcNow);
    internal static IntegrationPayload Payload(string text, string source = "sheet") => new("sample.json", BinaryData.FromString(text), "application/json", new Dictionary<string, string> { ["sourceId"] = source, ["correlationId"] = "kept" });

    [Fact]
    public void Discovery_UnionsRowsAndFindsNestedAndHeaderlessFieldsWithoutSavingValues()
    {
        var result = ParsedDataMapping.Inspect("""[{"id":1,"a/b":{"~x":null},"items":[{"sku":"x"}]},{"later":true}]""", "Auto", "");
        Assert.Equal(2, result.RecordCount);
        Assert.Contains("/later", result.Fields);
        Assert.Contains("/a~1b/~0x", result.Fields);
        Assert.Contains("/items", result.Fields);
        Assert.Contains("/items/0/sku", result.Fields);
        Assert.Contains("/0", ParsedDataMapping.Inspect("""[["id",12,true],["next",null,false]]""", "Json", "").Fields);
        Assert.Equal(0, ParsedDataMapping.Inspect("[]", "Json", "").RecordCount);
        Assert.Contains("", ParsedDataMapping.Inspect("null", "Json", "").Fields);
    }

    [Fact]
    public void Mapping_PreservesTypesEmptyValuesNullNestedStructuresAndEscapedNames()
    {
        var json = MapJson(new() { SourcePath = "/id", TargetPath = "/order/id" },
            new() { SourcePath = "/amount", TargetPath = "/amount" }, new() { SourcePath = "/active", TargetPath = "/active" },
            new() { SourcePath = "/blank", TargetPath = "/blank" }, new() { SourcePath = "/nil", TargetPath = "/nil" },
            new() { SourcePath = "/items", TargetPath = "/items" }, new() { SourcePath = "/a~1b/~0name", TargetPath = "/out~1name" });
        var output = JsonNode.Parse(ParsedDataMapping.Map(json, """[{"id":"00123","amount":12.5,"active":true,"blank":"","nil":null,"items":[{"sku":"x"}],"a/b":{"~name":"exact"}}]"""))![0]!;
        Assert.Equal("00123", output["order"]!["id"]!.GetValue<string>());
        Assert.Equal(12.5m, output["amount"]!.GetValue<decimal>());
        Assert.True(output["active"]!.GetValue<bool>());
        Assert.Equal("", output["blank"]!.GetValue<string>());
        Assert.True(output.AsObject().ContainsKey("nil"));
        Assert.Null(output["nil"]);
        Assert.Equal("x", output["items"]![0]!["sku"]!.GetValue<string>());
        Assert.Equal("exact", output["out/name"]!.GetValue<string>());
    }

    [Fact]
    public void MissingValues_AreExplicitAndDifferentFromPresentNull()
    {
        var fields = new ParsedDataFieldMap[] { new() { SourcePath = "/absent", TargetPath = "/value" } };
        Assert.Contains("record 1", Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(MapJson(fields), "{}")).Message);
        fields[0].Missing = "Omit";
        Assert.Equal("[{}]", ParsedDataMapping.Map(MapJson(fields), "{}"));
        fields[0].Missing = "Null";
        Assert.Equal("[{\"value\":null}]", ParsedDataMapping.Map(MapJson(fields), "{}"));
        fields[0].Missing = "Error";
        Assert.Equal("[{\"value\":null}]", ParsedDataMapping.Map(MapJson(fields), "{\"absent\":null}"));
    }

    [Fact]
    public void RecordSelection_MapsArraysObjectsScalarsNullAndConstants()
    {
        var map = new ParsedDataMap { RecordPath = "/data/rows", Fields = [new() { SourcePath = "", TargetPath = "/value" }, new() { Kind = "Constant", TargetPath = "/tag", ConstantJson = "true" }] };
        var json = JsonSerializer.Serialize(map, ParsedDataMapping.JsonOptions);
        Assert.Equal("[{\"value\":1,\"tag\":true},{\"value\":null,\"tag\":true}]", ParsedDataMapping.Map(json, "{\"data\":{\"rows\":[1,null]}}"));
        Assert.Equal("[]", ParsedDataMapping.Map(json, "{\"data\":{\"rows\":[]}}"));
        Assert.Contains("not found", Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(json, "{}")).Message);
        Assert.Equal("[{\"text\":\"plain parsed text\"}]", ParsedDataMapping.Map(MapJson(new ParsedDataFieldMap { TargetPath = "/text" }), "plain parsed text", "Text"));
    }

    [Fact]
    public void XmlNormalization_UsesExpandedNamesAttributesAndRepeatedRecords()
    {
        const string xml = "<x:Orders xmlns:x='urn:orders'><x:Order id='1'><x:Amount>12</x:Amount></x:Order><x:Order id='2'><x:Amount>20</x:Amount></x:Order></x:Orders>";
        var path = "/{urn:orders}Orders/{urn:orders}Order";
        var inspection = ParsedDataMapping.Inspect(xml, "Auto", path);
        Assert.Equal(2, inspection.RecordCount);
        Assert.Contains("/@id", inspection.Fields);
        Assert.Contains("/{urn:orders}Amount", inspection.Fields);
        var map = new ParsedDataMap { RecordPath = path, Fields = [new() { SourcePath = "/@id", TargetPath = "/id" }, new() { SourcePath = "/{urn:orders}Amount", TargetPath = "/amount" }] };
        var json = JsonSerializer.Serialize(map, ParsedDataMapping.JsonOptions);
        var result = ParsedDataMapping.Map(json, xml);
        Assert.Equal("[{\"id\":\"1\",\"amount\":\"12\"},{\"id\":\"2\",\"amount\":\"20\"}]", result);
        Assert.Equal(result, ParsedDataMapping.Map(json, inspection.ParsedData, "Json"));
        Assert.Equal("\"x\"", ParsedDataMapping.Normalize("<r xmlns='u'>x</r>")!["{u}r"]!.ToJsonString());
        Assert.Equal("  ", ParsedDataMapping.Normalize("<r>  </r>")!["r"]!.GetValue<string>());
        Assert.Equal("  ", ParsedDataMapping.Normalize("<r id='x'>  </r>")!["r"]!["#text"]!.GetValue<string>());
        Assert.Contains("DTD", Assert.Throws<ArgumentException>(() => ParsedDataMapping.Normalize("<!DOCTYPE x SYSTEM 'file:///not-read'><x/>" )).Message);
    }

    [Theory]
    [InlineData("/a", "/a")]
    [InlineData("/a", "/a/b")]
    [InlineData("/a/b", "/a")]
    public void InvalidMappings_RejectOverlappingTargets(string first, string second)
        => Assert.Contains("overlapping", Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(MapJson(new() { TargetPath = first }, new() { TargetPath = second }))).Message);

    [Theory]
    [InlineData("{\"version\":2}")]
    [InlineData("{\"fields\":null}")]
    [InlineData("{\"fields\":[null]}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("{\"fields\":[{\"targetPath\":\"bad\"}]}")]
    [InlineData("{\"fields\":[{\"targetPath\":\"/a~2b\"}]}")]
    [InlineData("{\"fields\":[{\"targetPath\":\"/a\",\"missing\":\"Ignore\"}]}")]
    [InlineData("{\"fields\":[{\"targetPath\":\"/a\",\"kind\":\"Function\"}]}")]
    [InlineData("{\"fields\":[{\"targetPath\":\"/a\",\"kind\":\"Constant\",\"constantJson\":\"bad\"}]}")]
    public void InvalidDefinitions_AreRejected(string json) => Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(json));

    [Fact]
    public void LimitsAndCancellation_AreEnforcedWithoutTruncation()
    {
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.Normalize(new string('x', 1_000_001), "Text"));
        var sample = new JsonObject();
        for (var index = 0; index < 256; index++) sample[index.ToString()] = index;
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.Inspect(sample.ToJsonString(), "Json", ""));
        var fields = Enumerable.Range(0, 257).Select(index => new ParsedDataFieldMap { TargetPath = "/v" + index }).ToArray();
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(MapJson(fields)));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => ParsedDataMapping.Map(MapJson(new ParsedDataFieldMap { TargetPath = "/v" }), "{}", cancellationToken: cts.Token));
        var large = JsonSerializer.Serialize(new string('x', 900_000));
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(MapJson(Enumerable.Range(0, 5).Select(index => new ParsedDataFieldMap { TargetPath = "/v" + index }).ToArray()), large));
    }

    [Fact]
    public void OutputSize_AcceptsExactlyTheLimitAndRejectsLargerResults()
    {
        var mapping = MapJson(new[] { "aaa", "b", "c", "d", "e" }.Select(name => new ParsedDataFieldMap { SourcePath = "/v", TargetPath = "/" + name }).ToArray());
        var input = new JsonObject { ["v"] = new string('x', 799_992) };
        Assert.Equal(4_000_000, ParsedDataMapping.Map(mapping, input.ToJsonString()).Length);
        input["v"] = new string('x', 799_993);
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(mapping, input.ToJsonString()));
    }

    [Fact]
    public async Task FlatFileParser_MapsAllPayloadsWithoutASecondSource()
    {
        var parser = new Mulse.CompatibilityPack.FlatFileParseModule();
        var parsed = await parser.ParseAsync(Context, new([Payload("Id,Amount\n001,12"), Payload("Id,Amount\n002,20")]), new(), default);
        var mapping = new ParsedDataMap { RecordPath = "/records", Fields = [new() { SourcePath = "/Id", TargetPath = "/Identifier" }] };
        var step = new ModuleStepDefinition { Settings = new() { ["mappingJson"] = JsonSerializer.Serialize(mapping, ParsedDataMapping.JsonOptions) } };
        var result = await new ParsedDataMapAugmentModule().AugmentAsync(Context, parsed, step, default);
        Assert.Equal(2, result.Count);
        Assert.Equal("001", JsonNode.Parse(result.Payloads[0].GetText())![0]!["Identifier"]!.GetValue<string>());
        Assert.Equal("002", JsonNode.Parse(result.Payloads[1].GetText())![0]!["Identifier"]!.GetValue<string>());
    }

    internal static IModuleInvoker TransformInvoker() => new ModuleInvoker(new ServiceCollection()
        .AddSingleton<IModuleCatalog>(new FakeExecutableModuleCatalog()
            .AddTransform(new ConcatTransformModule()).AddTransform(new MultiplyTransformModule()).AddTransform(new UpperCaseTransformModule()))
        .BuildServiceProvider());

    [Fact]
    public async Task Transforms_CombineOrderedFieldsAndTypedConstants_WithRuntimePreviewParity()
    {
        var mapping = MapJson(new ParsedDataFieldMap
        {
            Kind = "Transform", TargetPath = "/name", TransformModule = "concat-transform", TransformSettings = new() { ["separator"] = "-" },
            TransformInputs = [new() { SourcePath = "/first" }, new() { Kind = "Constant", ConstantJson = "\"fixed\"" }, new() { SourcePath = "/last" }]
        }, new ParsedDataFieldMap
        {
            Kind = "Transform", TargetPath = "/total", TransformModule = "multiply-transform",
            TransformInputs = [new() { SourcePath = "/amount" }, new() { Kind = "Constant", ConstantJson = "2" }]
        });
        const string input = """[{"first":"A","last":"B","amount":12.5},{"first":"C","last":"D","amount":3}]""";
        var invoker = TransformInvoker();
        var preview = await ParsedDataMapping.MapAsync(mapping, input, moduleInvoker: invoker, context: Context);
        var unrelated = Payload("{}", "other");
        var runtime = await new ParsedDataMapAugmentModule(invoker).AugmentAsync(Context, new([Payload(input), unrelated]),
            new() { Settings = new() { ["mappingJson"] = mapping, ["sourceId"] = "sheet" } }, default);
        Assert.Equal("""[{"name":"A-fixed-B","total":25.0},{"name":"C-fixed-D","total":6}]""", preview);
        Assert.Equal(preview, runtime.Payloads[0].GetText());
        Assert.Equal("kept", runtime.Payloads[0].Metadata["correlationId"]);
        Assert.Same(unrelated, runtime.Payloads[1]);
        Assert.Throws<InvalidOperationException>(() => ParsedDataMapping.Map(mapping, input));
    }

    [Fact]
    public async Task HostContainer_InjectsInvokerIntoParsedMap_AndExecutesCatalogTransforms()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddMulsePlatform(new ConfigurationBuilder().Build(), AppContext.BaseDirectory);
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment());
        services.AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        await using var provider = services.BuildServiceProvider();
        var catalog = provider.GetRequiredService<IModuleCatalog>();
        await using var lease = await catalog.LeaseOrchestrationAugmentAsync("parsed-data-map-augment", default);
        var mapping = MapJson(new ParsedDataFieldMap
        {
            Kind = "Transform", TargetPath = "/value", TransformModule = "uppercase-transform", TransformInputs = [new() { SourcePath = "/name" }]
        });
        var output = await lease.Module.AugmentAsync(Context, new([Payload("{\"name\":\"Ada\"}")]), new() { Settings = new() { ["mappingJson"] = mapping } }, default);
        Assert.Equal("[{\"value\":\"ADA\"}]", Assert.Single(output.Payloads).GetText());
    }

    [Fact]
    public async Task TransformMissingInputs_ApplyMappingPolicy_WithoutInvokingOmittedMappings()
    {
        var field = new ParsedDataFieldMap
        {
            Kind = "Transform", TargetPath = "/value", TransformModule = "observed",
            TransformInputs = [new() { SourcePath = "/absent" }]
        };
        var calls = 0;
        var invoker = new ObservedInvoker((inputs, _) => { calls++; return Task.FromResult(inputs[0]); });
        Assert.Contains("record 1", (await Assert.ThrowsAsync<ArgumentException>(() => ParsedDataMapping.MapAsync(MapJson(field), "{}", moduleInvoker: invoker, context: Context))).Message);
        field.Missing = "Omit";
        Assert.Equal("[{}]", await ParsedDataMapping.MapAsync(MapJson(field), "{}", moduleInvoker: invoker, context: Context));
        Assert.Equal(0, calls);
        field.Missing = "Null";
        Assert.Equal("[{\"value\":null}]", await ParsedDataMapping.MapAsync(MapJson(field), "{}", moduleInvoker: invoker, context: Context));
        field.Missing = "Error";
        Assert.Equal("[{\"value\":null}]", await ParsedDataMapping.MapAsync(MapJson(field), "{\"absent\":null}", moduleInvoker: invoker, context: Context));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("\"transformModule\":\"\"")]
    [InlineData("\"transformInputs\":null")]
    [InlineData("\"transformInputs\":[null]")]
    [InlineData("\"transformInputs\":[{\"kind\":\"Transform\"}]")]
    [InlineData("\"transformInputs\":[{\"sourcePath\":\"bad\"}]")]
    [InlineData("\"transformInputs\":[{\"kind\":\"Constant\",\"constantJson\":null}]")]
    [InlineData("\"transformSettings\":null")]
    [InlineData("\"transformSettings\":{\"separator\":null}")]
    public void InvalidTransforms_AreRejected(string property)
    {
        var json = "{\"fields\":[{\"kind\":\"Transform\",\"targetPath\":\"/v\",\"transformModule\":\"concat-transform\"," + property + "}]}";
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(json));
    }

    [Fact]
    public async Task Transforms_RespectCancellationFailuresSizeLimitsAndInputIsolation()
    {
        var field = new ParsedDataFieldMap
        {
            Kind = "Transform", TargetPath = "/changed", TransformModule = "observed", TransformInputs = [new()]
        };
        var mapping = MapJson(field, new() { SourcePath = "/value", TargetPath = "/original" });
        var invoker = new ObservedInvoker((inputs, _) =>
        {
            inputs[0]!["value"] = 2;
            return Task.FromResult(inputs[0]!["value"]);
        });
        Assert.Equal("[{\"changed\":2,\"original\":1}]", await ParsedDataMapping.MapAsync(mapping, "{\"value\":1}", moduleInvoker: invoker, context: Context));
        invoker = new ObservedInvoker((_, _) => throw new ArgumentException("transform failed"));
        Assert.Equal("transform failed", (await Assert.ThrowsAsync<ArgumentException>(() => ParsedDataMapping.MapAsync(mapping, "{}", moduleInvoker: invoker, context: Context))).Message);
        invoker = new ObservedInvoker((_, _) => Task.FromResult<JsonNode?>(JsonValue.Create(new string('x', 4_000_000))));
        Assert.Contains("4000000", (await Assert.ThrowsAsync<ArgumentException>(() => ParsedDataMapping.MapAsync(mapping, "{}", moduleInvoker: invoker, context: Context))).Message);
        using var cts = new CancellationTokenSource();
        invoker = new ObservedInvoker(async (_, token) => { cts.Cancel(); await Task.Delay(1, token); return null; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ParsedDataMapping.MapAsync(mapping, "{}", cancellationToken: cts.Token, moduleInvoker: invoker, context: Context));
        field.TransformInputs = Enumerable.Range(0, 257).Select(_ => new ParsedDataTransformInput()).ToList();
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(MapJson(field)));
    }

    private sealed class ObservedInvoker(Func<IReadOnlyList<JsonNode?>, CancellationToken, Task<JsonNode?>> invoke) : IModuleInvoker
    {
        public Task<JsonNode?> InvokeTransformAsync(string id, FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs,
            IReadOnlyDictionary<string, string> settings, CancellationToken ct) => invoke(inputs, ct);
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("")]
    [InlineData("removed")]
    public async Task FlowValidation_RejectsDanglingMapSources(string sourceId)
    {
        var catalog = FakeModuleCatalog.WithModules(("file-system-fetch", Mulse.Modules.Abstractions.ModuleKind.Fetch),
            ("json-parse", Mulse.Modules.Abstractions.ModuleKind.Parse), ("json-render", Mulse.Modules.Abstractions.ModuleKind.Render),
            ("logging-deliver", Mulse.Modules.Abstractions.ModuleKind.Deliver), ("parsed-data-map-augment", Mulse.Modules.Abstractions.ModuleKind.OrchestrationAugment));
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState());
        var service = new FlowDefinitionService(store, catalog);
        var flow = new PipelineDefinition
        {
            Id = "mapped", Fetches = [new() { Id = "fetch", Fetch = new() { Module = "file-system-fetch" } }],
            Parses = [new() { Id = "primary", FetchNodeIds = ["fetch"], Parse = new() { Module = "json-parse" } }],
            Augments = [new() { Module = "parsed-data-map-augment", Settings = new() { ["sourceId"] = sourceId } }],
            Deliveries = [new() { Render = new() { Module = "json-render" }, Delivers = [new() { Deliver = new() { Module = "logging-deliver" } }] }]
        };
        if (sourceId == "removed")
        {
            var error = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(flow, default));
            Assert.Contains("missing source", error.Message);
            Assert.Empty(store.GetState().Pipelines);
        }
        else Assert.Equal("mapped", (await service.CreateAsync(flow, default)).Id);
    }

    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public async Task Excel_ParseMapRender_RoundTripsRenamedTypedColumns(string format)
    {
        var workbookStep = new ModuleStepDefinition { Settings = new() { ["format"] = format, ["sheetName"] = "Orders" } };
        var input = new IntegrationBatch([Payload("""[{"Order.Id":"00123","Amount":12.5,"Active":true}]""")]);
        var workbook = await new ExcelRenderModule().RenderAsync(Context, input, workbookStep, default);
        var parsed = await new ExcelParseModule().ParseAsync(Context, workbook, workbookStep, default);
        var mapping = MapJson(new() { SourcePath = "/Order.Id", TargetPath = "/Id" }, new() { SourcePath = "/Amount", TargetPath = "/Total" }, new() { SourcePath = "/Active", TargetPath = "/Enabled" });
        var module = new ParsedDataMapAugmentModule();
        var step = new ModuleStepDefinition { Module = module.Descriptor.Id, Settings = new() { ["mappingJson"] = mapping, ["sourceId"] = "sheet" } };
        var unrelated = Payload("{\"other\":1}", "lookup");
        var result = await module.AugmentAsync(Context, new(parsed.Payloads.Append(unrelated).ToArray()), step, default);
        Assert.Same(unrelated, result.Payloads[1]);
        Assert.Equal("kept", result.Payloads[0].Metadata["correlationId"]);
        Assert.Equal("sheet", result.Payloads[0].Metadata["sourceId"]);
        var mappedWorkbook = await new ExcelRenderModule().RenderAsync(Context, new([result.Payloads[0]]), workbookStep, default);
        var final = await new ExcelParseModule().ParseAsync(Context, mappedWorkbook, workbookStep, default);
        var row = JsonNode.Parse(final.Payloads[0].GetText())![0]!;
        Assert.Equal("00123", row["Id"]!.GetValue<string>());
        Assert.Equal(12.5, row["Total"]!.GetValue<double>());
        Assert.True(row["Enabled"]!.GetValue<bool>());
        Assert.Null(row["Order.Id"]);
    }
}
