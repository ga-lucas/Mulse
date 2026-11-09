using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class XmlFormulaUiTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static XmlValueExpressionViewModel Parse(string text)
    {
        Assert.True(XmlFormula.TryParse(text, out var expression, out var error), error);
        return expression!;
    }

    [Fact]
    public void FunctionListLimitsAndArities_MatchTheEngineCatalog()
    {
        var names = Enum.GetNames<XmlMappingFunction>();
        Assert.Equal(names.Order(StringComparer.Ordinal), XmlFormula.Functions.Keys.Order(StringComparer.Ordinal));
        foreach (var function in Enum.GetValues<XmlMappingFunction>())
            Assert.Equal(XmlMappingFunctionCatalog.Arity(function), XmlFormula.Functions[function.ToString()]);
        Assert.Equal(XmlMappingFunctionCatalog.MaxDepth, XmlFormula.MaxDepth);
        Assert.Equal(XmlMappingFunctionCatalog.MaxNodes, XmlFormula.MaxNodes);
        Assert.Equal(XmlMappingFunctionCatalog.MaxConstantLength, XmlFormula.MaxConstantLength);
    }

    [Theory]
    [InlineData("StringConcat(/s:Order/s:Id, '-', StringUpperCase(/s:Order/@code))")]
    [InlineData("LogicalEq(/s:Order/s:Id, 'it''s')")]
    [InlineData("DateCurrentDate()")]
    [InlineData("MathDivide('10', '4')")]
    [InlineData("StringConcat('', '')")]
    public void FormatAndParse_RoundTrip(string text)
        => Assert.Equal(text, XmlFormula.Format(Parse(text)));

    [Fact]
    public void BareNumbersAndWhitespace_ParseAsConstants()
    {
        var expression = Parse("  StringLeft ( /s:A/s:B ,  -12.5 ) ");
        Assert.Equal("StringLeft", expression.Function);
        Assert.Equal("-12.5", expression.Arguments[1].Constant);
        Assert.Equal("StringLeft(/s:A/s:B, '-12.5')", XmlFormula.Format(expression));
        Assert.Equal(["/s:A/s:B"], XmlFormula.SourcePaths(expression));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Nope(/a)")]
    [InlineData("StringSize()")]
    [InlineData("StringSize(/a, /b)")]
    [InlineData("StringSize(/a")]
    [InlineData("StringSize(/a) trailing")]
    [InlineData("StringSize('open)")]
    [InlineData("StringSize(/a//b)")]
    [InlineData("StringSize(/a/)")]
    [InlineData("StringSize(-)")]
    [InlineData("StringSize(a)")]
    [InlineData("StringSize(/a,)")]
    [InlineData("DateCurrentDate(")]
    public void MalformedFormulas_AreRejectedWithAPosition(string text)
    {
        Assert.False(XmlFormula.TryParse(text, out var expression, out var error));
        Assert.Null(expression);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void LimitsAreEnforced()
    {
        var nested = string.Concat(Enumerable.Repeat("StringUpperCase(", XmlFormula.MaxDepth)) + "'x'" + new string(')', XmlFormula.MaxDepth);
        Assert.False(XmlFormula.TryParse(nested, out _, out _));
        var wide = "StringConcat(" + string.Join(", ", Enumerable.Repeat("'x'", 33)) + ")";
        Assert.False(XmlFormula.TryParse(wide, out _, out _));
        Assert.False(XmlFormula.TryParse("StringSize('" + new string('x', XmlFormula.MaxConstantLength + 1) + "')", out _, out _));
        Assert.True(XmlFormula.TryParse("StringSize('" + new string('x', XmlFormula.MaxConstantLength) + "')", out _, out _));
    }

    [Fact]
    public async Task EditorFormula_SavesAsEngineJsonAndRunsNatively()
    {
        var editor = new XmlMappingEditor();
        Property(editor, "Value").SetValue(editor, JsonSerializer.Serialize(new XmlMappingDefinition
        {
            TargetRoot = "Out", SourceRoot = null,
            Fields = [new() { SourcePath = "/In/A", TargetPath = "B" }]
        }, Web));
        Invoke(editor, "OnParametersSet");
        var map = (XmlMappingViewModel)Field(editor, "_map").GetValue(editor)!;
        var field = map.Fields[0];

        await (Task)Invoke(editor, "SetKindAsync", field, "Formula")!;
        Assert.Equal(2, map.Version);
        Assert.Equal("StringConcat('', '')", XmlFormula.Format(field.Expression!));

        await (Task)Invoke(editor, "SetFormulaAsync", field, "StringConcat(/In/A, '-', StringUpperCase(/In/A))")!;
        Assert.Null(Field(editor, "_error").GetValue(editor));
        var saved = JsonSerializer.Deserialize<XmlMappingDefinition>(editor.Value, Web)!;
        Assert.Equal(XmlMappingFunction.StringConcat, saved.Fields[0].Expression!.Function);
        Assert.Equal("ab-AB", System.Xml.Linq.XDocument.Parse(new XmlMappingEngine(editor.Value).Transform("<In><A>ab</A></In>")).Root!.Element("B")!.Value);

        var before = editor.Value;
        await (Task)Invoke(editor, "SetFormulaAsync", field, "StringConcat(/In/A")!;
        Assert.Equal(before, editor.Value);
        Assert.Contains("Formula error", Assert.IsType<string>(Field(editor, "_error").GetValue(editor)));

        await (Task)Invoke(editor, "SetKindAsync", field, "Constant")!;
        Assert.Null(field.Expression);
        Assert.DoesNotContain("\"function\":\"StringConcat\"", editor.Value);
        Assert.Null(JsonSerializer.Deserialize<XmlMappingDefinition>(editor.Value, Web)!.Fields[0].Expression);
    }

    [Fact]
    public async Task ImportedExpressionMap_LoadsRendersTheFormulaAndDrawsItsSources()
    {
        var json = JsonSerializer.Serialize(new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Out",
            Fields = [new()
            {
                TargetPath = "B",
                Expression = new()
                {
                    Kind = XmlValueExpressionKind.Function, Function = XmlMappingFunction.StringConcat,
                    Arguments = [new() { Kind = XmlValueExpressionKind.Source, SourcePath = "/In/A" }, new() { Kind = XmlValueExpressionKind.Constant, Constant = "'x" }]
                }
            }]
        }, Web);
        var editor = new XmlMappingEditor();
        Property(editor, "Value").SetValue(editor, json);
        Invoke(editor, "OnParametersSet");
        Assert.Null(Field(editor, "_error").GetValue(editor));
        var map = Assert.IsType<XmlMappingViewModel>(Field(editor, "_map").GetValue(editor));
        Assert.Equal(["/In/A"], XmlMappingGraph.SourcePaths(map.Fields[0]));
        Assert.Contains(XmlMappingGraph.Nodes(map, null, true), node => node.Path == "/In/A");
        Assert.DoesNotContain(XmlMappingGraph.Nodes(map, null, true), node => node.Path == "/");

        using var http = new HttpClient(new NotFoundHandler()) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http))
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<XmlMappingEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(XmlMappingEditor.Value)] = json }))).ToHtmlString());
        Assert.Contains("Formula (functions)", html);
        Assert.Contains("XML mapping formula", html);
        Assert.Contains("StringConcat(/In/A, &#x27;&#x27;&#x27;x&#x27;)", html);
        Assert.DoesNotContain("original definition is preserved", html);
    }

    [Theory]
    [InlineData("{\"version\":1,\"targetRoot\":\"Out\",\"fields\":[{\"targetPath\":\"B\",\"expression\":{\"kind\":\"Constant\",\"constant\":\"x\"}}]}")]
    [InlineData("{\"version\":2,\"targetRoot\":\"Out\",\"fields\":[{\"targetPath\":\"B\",\"expression\":{\"kind\":\"Function\",\"function\":\"Nope\",\"arguments\":[]}}]}")]
    [InlineData("{\"version\":2,\"targetRoot\":\"Out\",\"fields\":[{\"targetPath\":\"B\",\"constant\":\"x\",\"expression\":{\"kind\":\"Constant\",\"constant\":\"x\"}}]}")]
    [InlineData("{\"version\":2,\"targetRoot\":\"Out\",\"fields\":[{\"targetPath\":\"B\",\"expression\":{\"kind\":\"Source\",\"sourcePath\":\"relative\"}}]}")]
    public void InvalidExpressionJson_IsPreservedWithAnError(string json)
    {
        var editor = new XmlMappingEditor();
        Property(editor, "Value").SetValue(editor, json);
        Invoke(editor, "OnParametersSet");
        Assert.Null(Field(editor, "_map").GetValue(editor));
        Assert.Contains("original definition is preserved", Assert.IsType<string>(Field(editor, "_error").GetValue(editor)));
        Assert.Equal(json, editor.Value);
    }

    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static PropertyInfo Property(object target, string name) => target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
    }
}
