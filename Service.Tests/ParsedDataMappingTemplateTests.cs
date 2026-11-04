using System.Text.Json;
using System.Text.Json.Nodes;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Mapping;

namespace Service.Tests;

public sealed class ParsedDataMappingTemplateTests
{
    private static string Json(params ParsedDataFieldMap[] fields) => ParsedDataMappingTests.MapJson(fields);
    private static Task<string> MapAsync(string json, string input, IModuleInvoker? invoker = null, CancellationToken ct = default)
        => ParsedDataMapping.MapAsync(json, input, cancellationToken: ct, moduleInvoker: invoker ?? ParsedDataMappingTests.TransformInvoker(), context: ParsedDataMappingTests.Context);

    [Fact]
    public async Task Templates_MapNestedObjectsAndRepeatedItems_WithCurrentParentAndRootScopes()
    {
        var mapping = Json(new()
        {
            Kind = "Object", SourcePath = "/customer", TargetPath = "/buyer",
            Fields = [new() { SourcePath = "/name", TargetPath = "/display" }]
        }, new()
        {
            Kind = "Array", SourcePath = "/orders", TargetPath = "/invoices",
            Fields =
            [
                new() { SourcePath = "/id", TargetPath = "/number" },
                new()
                {
                    Kind = "Array", SourcePath = "/lines", TargetPath = "/items",
                    Fields =
                    [
                        new() { SourcePath = "/sku", TargetPath = "/code" },
                        new() { SourceScope = "Parent", SourcePath = "/id", TargetPath = "/invoice" },
                        new() { SourceScope = "Root", SourcePath = "/customer/name", TargetPath = "/buyer" },
                        new()
                        {
                            Kind = "Transform", TargetPath = "/total", TransformModule = "multiply-transform",
                            TransformInputs = [new() { SourcePath = "/price" }, new() { SourcePath = "/quantity" }]
                        }
                    ]
                }
            ]
        });
        const string input = """{"customer":{"name":"Ada"},"orders":[{"id":"A","lines":[{"sku":"x","price":2,"quantity":3},{"sku":"y","price":4,"quantity":2}]},{"id":"B","lines":[]}]}""";
        var preview = await MapAsync(mapping, input);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""[{"buyer":{"display":"Ada"},"invoices":[{"number":"A","items":[{"code":"x","invoice":"A","buyer":"Ada","total":6},{"code":"y","invoice":"A","buyer":"Ada","total":8}]},{"number":"B","items":[]}]}]"""), JsonNode.Parse(preview)));
        var runtime = await new Mulse.Modules.BuiltIn.Augment.ParsedDataMapAugmentModule(ParsedDataMappingTests.TransformInvoker()).AugmentAsync(
            ParsedDataMappingTests.Context, new([ParsedDataMappingTests.Payload(input)]), new() { Settings = new() { ["mappingJson"] = mapping } }, default);
        Assert.Equal(preview, Assert.Single(runtime.Payloads).GetText());
    }

    [Fact]
    public void RepeatingScalarItems_AndNullEmptyMissingInputs_HaveExplicitSemantics()
    {
        var field = new ParsedDataFieldMap { Kind = "Array", SourcePath = "/items", TargetPath = "/rows", Fields = [new() { TargetPath = "/value" }] };
        Assert.Equal("[{\"rows\":[{\"value\":1},{\"value\":null},{\"value\":true}]}]", ParsedDataMapping.Map(Json(field), "{\"items\":[1,null,true]}"));
        Assert.Equal("[{\"rows\":[]}]", ParsedDataMapping.Map(Json(field), "{\"items\":[]}"));
        Assert.Equal("[{\"rows\":null}]", ParsedDataMapping.Map(Json(field), "{\"items\":null}"));
        Assert.Contains("must be an array", Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(Json(field), "{\"items\":1}")).Message);
        Assert.Contains("record 1 target /rows", Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(Json(field), "{}")).Message);
        field.Missing = "Omit";
        Assert.Equal("[{}]", ParsedDataMapping.Map(Json(field), "{}"));
        field.Missing = "Null";
        Assert.Equal("[{\"rows\":null}]", ParsedDataMapping.Map(Json(field), "{}"));
        field.Kind = "Object";
        Assert.Equal("[{\"rows\":{\"value\":1}}]", ParsedDataMapping.Map(Json(field), "{\"items\":1}"));
        Assert.Equal("[{\"rows\":null}]", ParsedDataMapping.Map(Json(field), "{\"items\":null}"));
    }

    [Theory]
    [InlineData("Exists", "null", "null", true)]
    [InlineData("Missing", "null", "null", false)]
    [InlineData("IsNull", "null", "null", true)]
    [InlineData("IsNotNull", "\"\"", "null", true)]
    [InlineData("Equals", "1", "\"1\"", false)]
    [InlineData("Equals", "true", "true", true)]
    [InlineData("Equals", "{\"x\":[1]}", "{\"x\":[1]}", true)]
    [InlineData("NotEquals", "1", "2", true)]
    [InlineData("GreaterThan", "\"12.5\"", "12", true)]
    [InlineData("GreaterOrEqual", "12", "12", true)]
    [InlineData("LessThan", "2", "3", true)]
    [InlineData("LessOrEqual", "3", "3", true)]
    [InlineData("Contains", "\"Alpha\"", "\"ph\"", true)]
    [InlineData("StartsWith", "\"Alpha\"", "\"alpha\"", false)]
    [InlineData("EndsWith", "\"Alpha\"", "\"ha\"", true)]
    public void Conditions_UseTypedValuesAndOrdinalText(string operation, string actual, string expected, bool matches)
    {
        var mapping = Json(new ParsedDataFieldMap
        {
            Kind = "Constant", TargetPath = "/matched", ConstantJson = "true",
            Conditions = [new() { SourcePath = "/value", Operator = operation, ValueJson = expected }]
        });
        Assert.Equal(matches ? "[{\"matched\":true}]" : "[{}]", ParsedDataMapping.Map(mapping, "{\"value\":" + actual + "}"));
        if (operation != "Missing") Assert.Equal("[{}]", ParsedDataMapping.Map(mapping, "{}"));
        else Assert.Equal("[{\"matched\":true}]", ParsedDataMapping.Map(mapping, "{}"));
    }

    [Fact]
    public async Task Conditions_SupportAllAnyAndOtherwise_WithoutReadingOrInvokingUnselectedBranches()
    {
        var field = new ParsedDataFieldMap
        {
            Kind = "Transform", TargetPath = "/value", TransformModule = "observed", TransformInputs = [new() { SourcePath = "/absent" }],
            Conditions = [new() { SourcePath = "/active", Operator = "Equals", ValueJson = "true" }, new() { SourcePath = "/missing" }],
            TransformChain = [new() { Module = "observed" }]
        };
        var invoker = new RecordingInvoker((_, _) => throw new InvalidOperationException("Skipped branch must not invoke transforms."));
        Assert.Equal("[{}]", await MapAsync(Json(field), "{\"active\":true}", invoker));
        field.Otherwise = "Null";
        Assert.Equal("[{\"value\":null}]", await MapAsync(Json(field), "{\"active\":true}", invoker));
        field.Otherwise = "Constant"; field.OtherwiseJson = "{\"fallback\":true}";
        Assert.Equal("[{\"value\":{\"fallback\":true}}]", await MapAsync(Json(field), "{\"active\":true}", invoker));
        Assert.Equal(0, invoker.Calls);
        field.Kind = "Constant"; field.ConstantJson = "\"chosen\""; field.TransformChain.Clear(); field.ConditionMatch = "Any";
        Assert.Equal("[{\"value\":\"chosen\"}]", await MapAsync(Json(field), "{\"active\":true}", invoker));
        field.Conditions.Clear();
        Assert.Equal("[{\"value\":\"chosen\"}]", await MapAsync(Json(field), "{}", invoker));
    }

    [Fact]
    public void Conditions_RejectIncompatibleTypesRatherThanSilentlyFailing()
    {
        var field = new ParsedDataFieldMap
        {
            TargetPath = "/v", Conditions = [new() { SourcePath = "/x", Operator = "GreaterThan", ValueJson = "0" }]
        };
        Assert.Contains("requires a number", Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(Json(field), "{\"x\":true}")).Message);
        field.Conditions[0].Operator = "Contains";
        Assert.Contains("requires string", Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(Json(field), "{\"x\":12}")).Message);
    }

    [Fact]
    public async Task Chains_CombinePreviousResultsFieldsAndConstants_InOrder()
    {
        var field = new ParsedDataFieldMap
        {
            SourcePath = "/name", TargetPath = "/label",
            TransformChain =
            [
                new() { Module = "uppercase-transform" },
                new()
                {
                    Module = "concat-transform", Settings = new() { ["separator"] = "-" },
                    Inputs = [new() { Kind = "Previous" }, new() { SourcePath = "/id", SourceScope = "Root" }, new() { Kind = "Constant", ConstantJson = "\"mapped\"" }]
                }
            ]
        };
        Assert.Equal("[{\"label\":\"ADA-001-mapped\"}]", await MapAsync(Json(field), "{\"name\":\"Ada\",\"id\":\"001\"}"));
        field.TransformChain.Reverse();
        Assert.Equal("[{\"label\":\"ADA-001-MAPPED\"}]", await MapAsync(Json(field), "{\"name\":\"Ada\",\"id\":\"001\"}"));
        field.Missing = "Omit";
        Assert.Equal("[{}]", await MapAsync(Json(field), "{\"name\":\"Ada\"}"));
        field.Missing = "Null";
        Assert.Equal("[{\"label\":\"ADA-MAPPED\"}]", await MapAsync(Json(field), "{\"name\":\"Ada\"}"));
        Assert.Throws<InvalidOperationException>(() => ParsedDataMapping.Map(Json(field), "{}"));
    }

    [Fact]
    public async Task Chains_IsolatePreviousValues_AndPropagateFailuresAndCancellation()
    {
        var field = new ParsedDataFieldMap { SourcePath = "/obj", TargetPath = "/changed", TransformChain = [new() { Module = "observed" }] };
        var invoker = new RecordingInvoker((inputs, _) => { inputs[0]!["x"] = 2; return Task.FromResult(inputs[0]!["x"]); });
        Assert.Equal("[{\"changed\":2,\"original\":{\"x\":1}}]", await MapAsync(Json(field, new() { SourcePath = "/obj", TargetPath = "/original" }), "{\"obj\":{\"x\":1}}", invoker));
        invoker = new RecordingInvoker((_, _) => throw new ArgumentException("chain error"));
        Assert.Equal("chain error", (await Assert.ThrowsAsync<ArgumentException>(() => MapAsync(Json(field), "{\"obj\":null}", invoker))).Message);
        using var cts = new CancellationTokenSource();
        invoker = new RecordingInvoker((_, _) => { cts.Cancel(); return Task.FromResult<JsonNode?>(null); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MapAsync(Json(field), "{\"obj\":null}", invoker, cts.Token));
    }

    [Theory]
    [InlineData("\"fields\":null")]
    [InlineData("\"fields\":[null]")]
    [InlineData("\"sourceScope\":\"Document\"")]
    [InlineData("\"inputFields\":[null]")]
    [InlineData("\"conditions\":null")]
    [InlineData("\"conditions\":[null]")]
    [InlineData("\"conditions\":[{\"operator\":\"Unknown\"}]")]
    [InlineData("\"conditions\":[{\"operator\":\"Equals\",\"valueJson\":\"bad\"}]")]
    [InlineData("\"conditionMatch\":\"None\"")]
    [InlineData("\"otherwise\":\"Unknown\"")]
    [InlineData("\"otherwise\":\"Constant\",\"otherwiseJson\":null")]
    [InlineData("\"transformChain\":null")]
    [InlineData("\"transformChain\":[null]")]
    [InlineData("\"transformChain\":[{}]")]
    [InlineData("\"transformChain\":[{\"module\":\"upper\",\"inputs\":null}]")]
    [InlineData("\"transformChain\":[{\"module\":\"upper\",\"inputs\":[{\"kind\":\"Transform\"}]}]")]
    public void InvalidTemplateDefinitions_AreRejected(string property)
        => Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap("{\"fields\":[{\"targetPath\":\"/v\"," + property + "}]}"));

    [Fact]
    public void TemplateAndPipelineLimits_AcceptBoundariesAndRejectOverflow()
    {
        var root = new ParsedDataFieldMap { Kind = "Object", TargetPath = "/node" };
        var current = root;
        for (var depth = 1; depth < ParsedDataMapping.MaxMappingDepth; depth++)
        {
            var child = new ParsedDataFieldMap { Kind = "Object", TargetPath = "/node" };
            current.Fields.Add(child); current = child;
        }
        ParsedDataMapping.ReadMap(Json(root));
        Assert.Contains("node", ParsedDataMapping.Map(Json(root), "{}"));
        current.Fields.Add(new() { TargetPath = "/tooDeep" });
        Assert.Contains("8 levels", Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(Json(root))).Message);
        root.Fields = Enumerable.Range(0, 256).Select(index => new ParsedDataFieldMap { TargetPath = "/f" + index }).ToList();
        Assert.Contains("256 fields", Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(Json(root))).Message);
        root.Fields.RemoveAt(0);
        ParsedDataMapping.ReadMap(Json(root));
        root.Fields.Clear();
        root.Conditions = Enumerable.Range(0, 32).Select(_ => new ParsedDataCondition()).ToList();
        root.TransformChain = Enumerable.Range(0, 16).Select(_ => new ParsedDataTransformStep { Module = "uppercase-transform" }).ToList();
        ParsedDataMapping.ReadMap(Json(root));
        root.Conditions.Add(new());
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(Json(root)));
        root.Conditions.Clear(); root.TransformChain.Add(new() { Module = "uppercase-transform" });
        Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(Json(root)));
    }

    [Fact]
    public void NestedTargets_RejectOverlapAndCombinedPathDepthOverflow()
    {
        var field = new ParsedDataFieldMap
        {
            Kind = "Object", TargetPath = "/obj", Fields = [new() { TargetPath = "/a" }, new() { TargetPath = "/a/b" }]
        };
        Assert.Contains("overlapping", Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(Json(field))).Message);
        field.Fields = [new() { TargetPath = string.Concat(Enumerable.Repeat("/x", 31)) }];
        ParsedDataMapping.ReadMap(Json(field));
        field.Fields[0].TargetPath += "/tooDeep";
        Assert.Contains("32 levels", Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(Json(field))).Message);
        field.Kind = "Array"; field.Fields[0].TargetPath = string.Concat(Enumerable.Repeat("/x", 30));
        ParsedDataMapping.ReadMap(Json(field));
        field.Fields[0].TargetPath += "/tooDeep";
        Assert.Contains("32 levels", Assert.Throws<ArgumentException>(() => ParsedDataMapping.ReadMap(Json(field))).Message);
    }

    [Fact]
    public void RepeatingExecution_HasAnExactOperationBudget()
    {
        var mapping = Json(new ParsedDataFieldMap { Kind = "Array", SourcePath = "/items", TargetPath = "/rows" });
        var input = new JsonObject { ["items"] = new JsonArray(Enumerable.Repeat<JsonNode?>(null, ParsedDataMapping.MaxOperations - 2).ToArray()) };
        var result = JsonNode.Parse(ParsedDataMapping.Map(mapping, input.ToJsonString()));
        Assert.Equal(ParsedDataMapping.MaxOperations - 2, result![0]!["rows"]!.AsArray().Count);
        input["items"]!.AsArray().Add((JsonNode?)null);
        Assert.Contains("100000 operations", Assert.Throws<ArgumentException>(() => ParsedDataMapping.Map(mapping, input.ToJsonString())).Message);
    }

    [Fact]
    public void XmlRepeatingElements_MapUsingTheSameTemplateSemantics()
    {
        var map = new ParsedDataMap
        {
            RecordPath = "/Orders", Fields = [new()
            {
                Kind = "Array", SourcePath = "/Order", TargetPath = "/invoices",
                Fields = [new() { SourcePath = "/@id", TargetPath = "/id" }, new() { SourcePath = "/Amount", TargetPath = "/total" }]
            }]
        };
        Assert.Equal("[{\"invoices\":[{\"id\":\"1\",\"total\":\"12\"},{\"id\":\"2\",\"total\":\"20\"}]}]",
            ParsedDataMapping.Map(JsonSerializer.Serialize(map, ParsedDataMapping.JsonOptions), "<Orders><Order id='1'><Amount>12</Amount></Order><Order id='2'><Amount>20</Amount></Order></Orders>"));
    }

    private sealed class RecordingInvoker(Func<IReadOnlyList<JsonNode?>, CancellationToken, Task<JsonNode?>> invoke) : IModuleInvoker
    {
        public int Calls;
        public Task<JsonNode?> InvokeTransformAsync(string id, FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs, IReadOnlyDictionary<string, string> settings, CancellationToken ct)
        { Calls++; return invoke(inputs, ct); }
    }
}
