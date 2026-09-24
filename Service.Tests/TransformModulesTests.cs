using System.Text.Json.Nodes;

namespace Service.Tests;

public sealed class TransformModulesTests
{
    private static readonly FlowExecutionContext Context = new("flow-transform-tests", "exec-1", DateTimeOffset.UtcNow);

    private static Task<JsonNode?> InvokeAsync(ITransformModule module, params JsonNode?[] inputs)
        => InvokeWithSettingsAsync(module, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), inputs);

    private static Task<JsonNode?> InvokeWithSettingsAsync(ITransformModule module, Dictionary<string, string> settings, params JsonNode?[] inputs)
        => module.InvokeAsync(
            Context,
            inputs,
            new ModuleStepDefinition { Module = module.Descriptor.Id, Settings = new Dictionary<string, string>(settings, StringComparer.OrdinalIgnoreCase) },
            CancellationToken.None);

    private static Dictionary<string, string> Settings(params (string Key, string Value)[] values)
        => values.ToDictionary(static value => value.Key, static value => value.Value, StringComparer.OrdinalIgnoreCase);

    private static JsonNode? Json(string json) => JsonNode.Parse(json);

    private static decimal Number(JsonNode? node) => Assert.IsAssignableFrom<JsonValue>(node).GetValue<decimal>();

    private static string Text(JsonNode? node) => Assert.IsAssignableFrom<JsonValue>(node).GetValue<string>();

    [Fact]
    public void BuiltInModuleInstaller_RegistersEveryTransformWithMatchingDescriptorKind()
    {
        var builder = new ModuleRegistryBuilder();
        new BuiltInModuleInstaller().Install(builder);

        var transformTypes = builder.Registrations
            .Where(static registration => registration.Kind == ModuleKind.Transform)
            .Select(static registration => registration.ImplementationType)
            .ToArray();

        var descriptors = transformTypes
            .Select(static type => ((ITransformModule)Activator.CreateInstance(type)!).Descriptor)
            .ToArray();

        Assert.All(descriptors, static descriptor => Assert.Equal(ModuleKind.Transform, descriptor.Kind));
        Assert.Equal(
            new[]
            {
                "add-transform", "coalesce-transform", "concat-transform", "divide-transform", "format-date-transform",
                "lowercase-transform", "multiply-transform", "round-transform", "substring-transform",
                "subtract-transform", "trim-transform", "uppercase-transform"
            },
            descriptors.Select(static descriptor => descriptor.Id).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Concat_SkipsNullsByDefault_AndUsesSeparator()
    {
        var result = await InvokeWithSettingsAsync(new ConcatTransformModule(), Settings(("separator", "-")), Json("\"a\""), null, Json("1"), Json("true"));

        Assert.Equal("a-1-True", Text(result));
    }

    [Fact]
    public async Task Concat_TreatsNullsAsEmpty_WhenSkipNullsIsFalse()
    {
        var result = await InvokeWithSettingsAsync(new ConcatTransformModule(), Settings(("separator", "-"), ("skipNulls", "false")), Json("\"a\""), null, Json("\"b\""));

        Assert.Equal("a--b", Text(result));
    }

    [Fact]
    public async Task Concat_HonorsWhitespaceSeparator_AndAcceptsZeroInputs()
    {
        var module = new ConcatTransformModule();

        Assert.Equal("Ada Lovelace", Text(await InvokeWithSettingsAsync(module, Settings(("separator", " ")), Json("\"Ada\""), Json("\"Lovelace\""))));
        Assert.Equal(string.Empty, Text(await InvokeAsync(module)));
    }

    [Fact]
    public async Task Add_SumsNumbersAndNumericText_TreatingOtherInputsAsZero()
    {
        var module = new AddTransformModule();

        Assert.Equal(3.5m, Number(await InvokeAsync(module, Json("1"), Json("\"2.5\""), null, Json("\"abc\""))));
        Assert.Equal(0.3m, Number(await InvokeAsync(module, Json("0.1"), Json("0.2"))));
        Assert.Equal(0m, Number(await InvokeAsync(module)));
    }

    [Fact]
    public async Task Subtract_SubtractsRemainingInputsFromFirst()
    {
        var module = new SubtractTransformModule();

        Assert.Equal(5m, Number(await InvokeAsync(module, Json("10"), Json("3"), Json("\"2\""))));
        Assert.Equal(7m, Number(await InvokeAsync(module, Json("7"))));
        Assert.Null(await InvokeAsync(module, Json("\"not a number\""), Json("1")));
    }

    [Fact]
    public async Task Subtract_RequiresAtLeastOneInput()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() => InvokeAsync(new SubtractTransformModule()));

        Assert.Contains("subtract-transform", exception.Message);
    }

    [Fact]
    public async Task Multiply_SkipsNonNumericInputs_AndReturnsNullWhenNothingIsNumeric()
    {
        var module = new MultiplyTransformModule();

        Assert.Equal(6m, Number(await InvokeAsync(module, Json("2"), Json("\"3\""), null, Json("\"x\""))));
        Assert.Null(await InvokeAsync(module));
        Assert.Null(await InvokeAsync(module, Json("\"x\""), null));
    }

    [Fact]
    public async Task Divide_DividesSequentially_AndReturnsNullOnDivisionByZeroOrNonNumericInput()
    {
        var module = new DivideTransformModule();

        Assert.Equal(2.5m, Number(await InvokeAsync(module, Json("10"), Json("4"))));
        Assert.Equal(1m, Number(await InvokeAsync(module, Json("20"), Json("4"), Json("5"))));
        Assert.Equal(10m, Number(await InvokeAsync(module, Json("10"))));
        Assert.Null(await InvokeAsync(module, Json("10"), Json("0")));
        Assert.Null(await InvokeAsync(module, Json("10"), null));
        await Assert.ThrowsAsync<ArgumentException>(() => InvokeAsync(module));
    }

    [Fact]
    public async Task Coalesce_ReturnsFirstNonNullNonBlankInput()
    {
        var module = new CoalesceTransformModule();

        Assert.Equal("x", Text(await InvokeAsync(module, null, Json("\"  \""), Json("\"\""), Json("\"x\""), Json("\"y\""))));
        Assert.Equal(0m, Number(await InvokeAsync(module, null, Json("0"))));
        Assert.Null(await InvokeAsync(module, null, Json("\"\"")));
        Assert.Null(await InvokeAsync(module));
    }

    [Fact]
    public async Task Coalesce_ReturnsDetachedCopy()
    {
        var parent = new JsonObject { ["value"] = "kept" };

        var result = await InvokeAsync(new CoalesceTransformModule(), parent["value"]);

        Assert.Null(result!.Parent);
        Assert.Equal("kept", Text(result));
    }

    [Fact]
    public async Task Substring_ExtractsByStartAndOptionalLength_ClampingOutOfRangeValues()
    {
        var module = new SubstringTransformModule();
        var input = Json("\"Hello World\"");

        Assert.Equal("World", Text(await InvokeWithSettingsAsync(module, Settings(("start", "6")), input)));
        Assert.Equal("Hello", Text(await InvokeWithSettingsAsync(module, Settings(("start", "0"), ("length", "5")), input)));
        Assert.Equal("Hello World", Text(await InvokeWithSettingsAsync(module, Settings(("start", "-3"), ("length", "100")), input)));
        Assert.Equal(string.Empty, Text(await InvokeWithSettingsAsync(module, Settings(("start", "50")), input)));
        Assert.Equal(string.Empty, Text(await InvokeWithSettingsAsync(module, Settings(("start", "2"), ("length", "-1")), input)));
        Assert.Null(await InvokeWithSettingsAsync(module, Settings(("start", "0")), [null]));
    }

    [Fact]
    public async Task Substring_ValidatesArityAndRequiredStart()
    {
        var module = new SubstringTransformModule();

        var arity = await Assert.ThrowsAsync<ArgumentException>(() => InvokeWithSettingsAsync(module, Settings(("start", "0")), Json("\"a\""), Json("\"b\"")));
        Assert.Contains("substring-transform", arity.Message);
        Assert.Contains("exactly 1 input", arity.Message);

        var missingStart = await Assert.ThrowsAsync<ArgumentException>(() => InvokeAsync(module, Json("\"a\"")));
        Assert.Equal("Module 'substring-transform' requires the 'start' setting. (Parameter 'start')", missingStart.Message);
    }

    [Fact]
    public async Task CaseAndTrimTransforms_ConvertSingleTextInput()
    {
        Assert.Equal("ÄBC-1", Text(await InvokeAsync(new UpperCaseTransformModule(), Json("\"äbc-1\""))));
        Assert.Equal("abc", Text(await InvokeAsync(new LowerCaseTransformModule(), Json("\"AbC\""))));
        Assert.Equal("padded", Text(await InvokeAsync(new TrimTransformModule(), Json("\"  padded \\t\""))));
        Assert.Null(await InvokeAsync(new TrimTransformModule(), [null]));
    }

    [Fact]
    public async Task SingleInputTransforms_RejectWrongArity()
    {
        ITransformModule[] modules = [new UpperCaseTransformModule(), new LowerCaseTransformModule(), new TrimTransformModule(), new RoundTransformModule()];

        foreach (var module in modules)
        {
            var exception = await Assert.ThrowsAsync<ArgumentException>(() => InvokeAsync(module));
            Assert.Contains($"Module '{module.Descriptor.Id}' requires exactly 1 input but received 0.", exception.Message);
        }
    }

    [Fact]
    public async Task Round_RoundsMidpointsAwayFromZero()
    {
        var module = new RoundTransformModule();

        Assert.Equal(2.35m, Number(await InvokeWithSettingsAsync(module, Settings(("decimals", "2")), Json("2.345"))));
        Assert.Equal(3m, Number(await InvokeAsync(module, Json("2.5"))));
        Assert.Equal(-3m, Number(await InvokeAsync(module, Json("\"-2.5\""))));
        Assert.Null(await InvokeAsync(module, Json("\"abc\"")));
        await Assert.ThrowsAsync<ArgumentException>(() => InvokeWithSettingsAsync(module, Settings(("decimals", "29")), Json("1")));
    }

    [Fact]
    public async Task FormatDate_FormatsParsedDates_AndReturnsNullForNonDates()
    {
        var module = new FormatDateTransformModule();

        Assert.Equal("2024-03-05", Text(await InvokeWithSettingsAsync(module, Settings(("outputFormat", "yyyy-MM-dd")), Json("\"2024-03-05T13:45:00Z\""))));
        Assert.Equal("31/12/2024 00:00", Text(await InvokeWithSettingsAsync(module, Settings(("outputFormat", "dd/MM/yyyy HH:mm")), Json("\"2024-12-31\""))));
        Assert.Null(await InvokeWithSettingsAsync(module, Settings(("outputFormat", "yyyy-MM-dd")), Json("\"not a date\"")));
        Assert.Null(await InvokeWithSettingsAsync(module, Settings(("outputFormat", "yyyy-MM-dd")), [null]));
    }

    [Fact]
    public async Task FormatDate_ValidatesArityAndRequiredFormat()
    {
        var module = new FormatDateTransformModule();

        await Assert.ThrowsAsync<ArgumentException>(() => InvokeWithSettingsAsync(module, Settings(("outputFormat", "yyyy")), Json("\"2024-01-01\""), Json("\"2024-01-02\"")));
        var missingFormat = await Assert.ThrowsAsync<ArgumentException>(() => InvokeAsync(module, Json("\"2024-01-01\"")));
        Assert.Contains("'outputFormat'", missingFormat.Message);
    }
}
