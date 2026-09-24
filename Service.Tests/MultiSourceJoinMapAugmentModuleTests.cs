using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Service.Tests;

public sealed class MultiSourceJoinMapAugmentModuleTests
{
    private const string ModuleId = "multi-source-join-map-augment";

    private static readonly FlowExecutionContext Context = new("flow-join-tests", "exec-1", DateTimeOffset.UtcNow);

    // Exactly the JSON shape the Designer writes into the 'mappingJson' setting (camelCase property names, string
    // enums, null members omitted).
    private const string DesignerShapedMappingJson =
        """
        [
          { "sourceKind": "Left", "sourcePath": "customerId", "targetField": "customerId", "condition": "Always" },
          {
            "sourceKind": "Transform", "sourcePath": "", "targetField": "fullName", "condition": "Always",
            "transformModule": "concat-transform",
            "transformInputs": [
              { "sourceKind": "Left", "sourcePath": "firstName" },
              { "sourceKind": "Left", "sourcePath": "lastName" }
            ],
            "transformSettings": { "separator": " " }
          },
          {
            "sourceKind": "Transform", "sourcePath": "", "targetField": "reference", "condition": "Always",
            "transformModule": "concat-transform",
            "transformInputs": [
              { "sourceKind": "Left", "sourcePath": "lastName" },
              { "sourceKind": "Right", "sourcePath": "orderId" },
              { "sourceKind": "Literal", "sourcePath": "", "literalValue": "\"web\"" }
            ],
            "transformSettings": { "separator": "/" }
          },
          {
            "sourceKind": "Transform", "sourcePath": "", "targetField": "order.lineTotal", "condition": "Always",
            "transformModule": "multiply-transform",
            "transformInputs": [
              { "sourceKind": "Right", "sourcePath": "qty" },
              { "sourceKind": "Right", "sourcePath": "price" }
            ]
          }
        ]
        """;

    private static IntegrationBatch CreateCustomerOrderBatch()
    {
        var customer = new IntegrationPayload(
            "customer.json",
            BinaryData.FromString("""{ "customerId": 1, "firstName": "Ada", "lastName": "Lovelace" }"""),
            "application/json",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sourceId"] = "customers" });
        var orders = new IntegrationPayload(
            "orders.json",
            BinaryData.FromString("""[ { "customerId": 1, "orderId": "A1", "qty": 2, "price": "2.50" }, { "customerId": 1, "orderId": "A2", "qty": 3, "price": 10 } ]"""),
            "application/json",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sourceId"] = "orders" });

        return new IntegrationBatch([customer, orders]);
    }

    private static ModuleStepDefinition CreateStep(string mappingJson, string cardinality = "FanOut", string? nestedTargetPath = null)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["leftSourceId"] = "customers",
            ["rightSourceId"] = "orders",
            ["joinKeysJson"] = """[ { "leftPath": "customerId", "rightPath": "customerId" } ]""",
            ["joinKind"] = "Inner",
            ["resultCardinality"] = cardinality,
            ["mappingJson"] = mappingJson
        };

        if (nestedTargetPath is not null)
        {
            settings["nestedTargetPath"] = nestedTargetPath;
        }

        return new ModuleStepDefinition { Module = ModuleId, Settings = settings };
    }

    private static IModuleInvoker CreateBuiltInTransformInvoker()
    {
        var catalog = new FakeExecutableModuleCatalog()
            .AddTransform(new ConcatTransformModule())
            .AddTransform(new MultiplyTransformModule())
            .AddTransform(new UpperCaseTransformModule());
        var services = new ServiceCollection()
            .AddSingleton<IModuleCatalog>(catalog)
            .BuildServiceProvider();
        return new ModuleInvoker(services);
    }

    private static JsonObject[] ReadDocuments(IntegrationBatch batch)
        => batch.Payloads.Select(static payload => JsonNode.Parse(payload.Content.ToString())!.AsObject()).ToArray();

    [Fact]
    public async Task AugmentAsync_TransformMappings_InvokeTransformModulesWithResolvedLeftRightAndLiteralInputs()
    {
        var module = new MultiSourceJoinMapAugmentModule(CreateBuiltInTransformInvoker());

        var result = await module.AugmentAsync(Context, CreateCustomerOrderBatch(), CreateStep(DesignerShapedMappingJson), CancellationToken.None);

        var documents = ReadDocuments(result);
        Assert.Equal(2, documents.Length);

        Assert.Equal(1, documents[0]["customerId"]!.GetValue<int>());
        Assert.Equal("Ada Lovelace", documents[0]["fullName"]!.GetValue<string>());
        Assert.Equal("Lovelace/A1/web", documents[0]["reference"]!.GetValue<string>());
        Assert.Equal(5m, documents[0]["order"]!["lineTotal"]!.GetValue<decimal>());

        Assert.Equal("Lovelace/A2/web", documents[1]["reference"]!.GetValue<string>());
        Assert.Equal(30m, documents[1]["order"]!["lineTotal"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task AugmentAsync_NestedCardinality_MapsRightScopedTransformsIntoEachNestedItem()
    {
        const string mappingJson =
            """
            [
              {
                "sourceKind": "Transform", "targetField": "customerName", "transformModule": "uppercase-transform",
                "transformInputs": [ { "sourceKind": "Left", "sourcePath": "lastName" } ]
              },
              {
                "sourceKind": "Transform", "targetField": "total", "transformModule": "multiply-transform",
                "transformInputs": [ { "sourceKind": "Right", "sourcePath": "qty" }, { "sourceKind": "Right", "sourcePath": "price" } ]
              },
              { "sourceKind": "Right", "sourcePath": "orderId", "targetField": "orderId" }
            ]
            """;
        var module = new MultiSourceJoinMapAugmentModule(CreateBuiltInTransformInvoker());

        var result = await module.AugmentAsync(Context, CreateCustomerOrderBatch(), CreateStep(mappingJson, "Nested", "lines"), CancellationToken.None);

        var document = Assert.Single(ReadDocuments(result));
        Assert.Equal("LOVELACE", document["customerName"]!.GetValue<string>());
        Assert.False(document.ContainsKey("total"));

        var lines = document["lines"]!.AsArray();
        Assert.Equal(2, lines.Count);
        Assert.Equal("A1", lines[0]!["orderId"]!.GetValue<string>());
        Assert.Equal(5m, lines[0]!["total"]!.GetValue<decimal>());
        Assert.Equal(30m, lines[1]!["total"]!.GetValue<decimal>());
        Assert.False(lines[0]!.AsObject().ContainsKey("customerName"));
    }

    [Fact]
    public async Task AugmentAsync_TransformMapping_PassesInputsInOrderAndSettings_AndSkipsNullResults()
    {
        var invoker = new RecordingModuleInvoker((moduleId, _, _) => moduleId == "null-transform" ? null : JsonValue.Create("computed"));
        const string mappingJson =
            """
            [
              {
                "sourceKind": "Transform", "targetField": "computed", "transformModule": "custom-transform",
                "transformInputs": [
                  { "sourceKind": "Right", "sourcePath": "orderId" },
                  { "sourceKind": "Literal", "literalValue": "42" },
                  { "sourceKind": "Left", "sourcePath": "firstName" },
                  { "sourceKind": "Left", "sourcePath": "doesNotExist" }
                ],
                "transformSettings": { "mode": "strict" }
              },
              {
                "sourceKind": "Transform", "targetField": "skipped", "transformModule": "null-transform"
              }
            ]
            """;
        var module = new MultiSourceJoinMapAugmentModule(invoker);
        var batch = new IntegrationBatch([CreateCustomerOrderBatch().Payloads[0], new IntegrationPayload(
            "order.json",
            BinaryData.FromString("""{ "customerId": 1, "orderId": "A1" }"""),
            "application/json",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sourceId"] = "orders" })]);

        var result = await module.AugmentAsync(Context, batch, CreateStep(mappingJson), CancellationToken.None);

        var document = Assert.Single(ReadDocuments(result));
        Assert.Equal("computed", document["computed"]!.GetValue<string>());
        Assert.False(document.ContainsKey("skipped"));

        Assert.Equal(2, invoker.Calls.Count);
        var call = invoker.Calls[0];
        Assert.Equal("custom-transform", call.ModuleId);
        Assert.Same(Context, call.Context);
        Assert.Equal(4, call.Inputs.Length);
        Assert.Equal("A1", call.Inputs[0]!.GetValue<string>());
        Assert.Equal(42, call.Inputs[1]!.GetValue<int>());
        Assert.Equal("Ada", call.Inputs[2]!.GetValue<string>());
        Assert.Null(call.Inputs[3]);
        Assert.Equal("strict", call.Settings["mode"]);
        Assert.Empty(invoker.Calls[1].Inputs);
    }

    [Fact]
    public async Task AugmentAsync_TransformMappingWithoutModule_ThrowsArgumentExceptionNamingTargetField()
    {
        const string mappingJson = """[ { "sourceKind": "Transform", "targetField": "fullName" } ]""";
        var module = new MultiSourceJoinMapAugmentModule(new RecordingModuleInvoker((_, _, _) => null));

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => module.AugmentAsync(Context, CreateCustomerOrderBatch(), CreateStep(mappingJson), CancellationToken.None));

        Assert.Contains("'transformModule'", exception.Message);
        Assert.Contains("'fullName'", exception.Message);
    }

    [Fact]
    public void WorkflowFieldMappingDefinition_WithTransformInputs_RoundTripsThroughModuleSerializerConventions()
    {
        // Mirrors MultiSourceJoinMapAugmentModule's private SerializerOptions (web defaults + string enums).
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());

        WorkflowFieldMappingDefinition[] original =
        [
            new()
            {
                SourceKind = WorkflowFieldSourceKind.Transform,
                TargetField = "fullName",
                Condition = WorkflowFieldCondition.WhenMatched,
                TransformModule = "concat-transform",
                TransformInputs =
                [
                    new WorkflowTransformInputDefinition { SourceKind = WorkflowFieldSourceKind.Left, SourcePath = "firstName" },
                    new WorkflowTransformInputDefinition { SourceKind = WorkflowFieldSourceKind.Right, SourcePath = "lines[].sku" },
                    new WorkflowTransformInputDefinition { SourceKind = WorkflowFieldSourceKind.Literal, LiteralValue = "\" \"" }
                ],
                TransformSettings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["separator"] = "-", ["skipNulls"] = "false" }
            }
        ];

        var json = JsonSerializer.Serialize(original, options);
        Assert.Contains("\"sourceKind\":\"Transform\"", json);
        Assert.Contains("\"transformInputs\":[", json);

        var roundTripped = Assert.Single(JsonSerializer.Deserialize<WorkflowFieldMappingDefinition[]>(json, options)!);
        Assert.Equal(WorkflowFieldSourceKind.Transform, roundTripped.SourceKind);
        Assert.Equal("fullName", roundTripped.TargetField);
        Assert.Equal(WorkflowFieldCondition.WhenMatched, roundTripped.Condition);
        Assert.Equal("concat-transform", roundTripped.TransformModule);
        Assert.Equal(original[0].TransformInputs, roundTripped.TransformInputs);
        Assert.Equal(original[0].TransformSettings, roundTripped.TransformSettings);

        var designerShaped = JsonSerializer.Deserialize<WorkflowFieldMappingDefinition[]>(DesignerShapedMappingJson, options)!;
        Assert.Equal(4, designerShaped.Length);
        Assert.Equal(WorkflowFieldSourceKind.Left, designerShaped[0].SourceKind);
        Assert.Empty(designerShaped[0].TransformInputs);
        Assert.Equal(
            new[] { WorkflowFieldSourceKind.Left, WorkflowFieldSourceKind.Right, WorkflowFieldSourceKind.Literal },
            designerShaped[2].TransformInputs.Select(static input => input.SourceKind));
        Assert.Equal("\"web\"", designerShaped[2].TransformInputs[2].LiteralValue);
        Assert.Equal("/", designerShaped[2].TransformSettings["separator"]);
    }

    [Fact]
    public async Task HostContainer_ResolvesJoinModuleWithInvoker_AndRunsBuiltInTransformsThroughTheCatalog()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMulsePlatform(new ConfigurationBuilder().Build(), AppContext.BaseDirectory);
        services.AddSingleton<IHostEnvironment>(new FakeHostEnvironment());
        services.AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        await using var provider = services.BuildServiceProvider();

        // Constructing the catalog instantiates every built-in module, including the join/map augment and its
        // IModuleInvoker dependency; this must not recurse back into the catalog.
        var catalog = provider.GetRequiredService<IModuleCatalog>();
        Assert.Contains(catalog.GetAll(), static entry => entry.Descriptor.Id == "concat-transform" && entry.Descriptor.Kind == ModuleKind.Transform);

        await using var lease = await catalog.LeaseOrchestrationAugmentAsync(ModuleId, CancellationToken.None);
        var result = await lease.Module.AugmentAsync(Context, CreateCustomerOrderBatch(), CreateStep(DesignerShapedMappingJson), CancellationToken.None);

        var documents = ReadDocuments(result);
        Assert.Equal("Ada Lovelace", documents[0]["fullName"]!.GetValue<string>());
        Assert.Equal(30m, documents[1]["order"]!["lineTotal"]!.GetValue<decimal>());
    }

    private sealed class RecordingModuleInvoker(Func<string, IReadOnlyList<JsonNode?>, IReadOnlyDictionary<string, string>, JsonNode?> invoke) : IModuleInvoker
    {
        public List<(string ModuleId, FlowExecutionContext Context, JsonNode?[] Inputs, Dictionary<string, string> Settings)> Calls { get; } = [];

        public Task<JsonNode?> InvokeTransformAsync(
            string moduleId,
            FlowExecutionContext context,
            IReadOnlyList<JsonNode?> inputs,
            IReadOnlyDictionary<string, string> settings,
            CancellationToken cancellationToken)
        {
            Calls.Add((moduleId, context, inputs.Select(static input => input?.DeepClone()).ToArray(), new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase)));
            return Task.FromResult(invoke(moduleId, inputs, settings));
        }
    }
}
