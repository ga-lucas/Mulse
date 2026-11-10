using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.CompatibilityPack.Mapping;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class XmlCustomFunctionUiTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static XmlCustomFunctionViewModel Stub(string name = "FormatDate", params string[] parameters) => new()
    {
        Name = name,
        Description = "Imported BizTalk scripting functoid.\nDetected behavior: date format yyyy-MM-dd.",
        Parameters = parameters.Select(parameter => new XmlCustomFunctionParameterViewModel { Name = parameter, Description = "string" }).ToList(),
        OriginalSource = "public string FormatDate(string raw) { return raw; }",
        OriginalSourceLanguage = "CSharp",
        Usages = ["Map 'm', functoid 10: inputs (source /Order/Id) -> feeds Number"]
    };

    private static XmlMappingViewModel MapWith(params XmlCustomFunctionViewModel[] functions) => new() { Version = 2, TargetRoot = "Out", CustomFunctions = functions.ToList() };

    private static XmlValueExpressionViewModel Parse(string text, XmlFormulaContext? context = null)
    {
        Assert.True(XmlFormula.TryParse(text, out var expression, out var error, context), error);
        return expression!;
    }

    [Fact]
    public void FunctionSummariesAndCustomLimits_MatchTheEngineCatalog()
    {
        foreach (var function in Enum.GetValues<XmlMappingFunction>())
            Assert.Equal(XmlMappingFunctionCatalog.Summary(function), XmlFormula.Summaries[function.ToString()]);
        Assert.Equal(XmlMappingFunctionCatalog.MaxCustomFunctions, XmlFormula.MaxCustomFunctions);
        Assert.Equal(XmlMappingFunctionCatalog.MaxCustomParameters, XmlFormula.MaxCustomParameters);
        Assert.Equal(XmlMappingFunctionCatalog.MaxOriginalSourceLength, XmlFormula.MaxOriginalSourceLength);
        Assert.Equal(XmlMappingFunctionCatalog.MaxDescriptionLength, XmlFormula.MaxDescriptionLength);
        Assert.Equal(XmlMappingFunctionCatalog.MaxUsages, XmlFormula.MaxUsages);
        Assert.Equal(XmlMappingFunctionCatalog.MaxUsageLength, XmlFormula.MaxUsageLength);
    }

    [Fact]
    public void CustomCallsAndParameters_ParseFormatAndCheckArity()
    {
        var context = XmlCustomFunctions.ContextFor([Stub("FormatDate", "raw", "pattern")]);
        var call = Parse("FormatDate(/s:Order/s:Id, 'yyyy')", context);
        Assert.Equal("Custom", call.Kind);
        Assert.Equal("FormatDate", call.Name);
        Assert.Equal("FormatDate(/s:Order/s:Id, 'yyyy')", XmlFormula.Format(call));
        Assert.False(XmlFormula.TryParse("FormatDate(/s:Order/s:Id)", out _, out var error, context));
        Assert.NotNull(error);
        Assert.False(XmlFormula.TryParse("Unknown(/s:Order/s:Id)", out _, out _, context));

        var body = XmlCustomFunctions.ContextForBody([Stub("FormatDate", "raw", "pattern")], Stub("FormatDate", "raw", "pattern"));
        var parsed = Parse("StringConcat($raw, '-', $pattern)", body);
        Assert.Equal("StringConcat($raw, '-', $pattern)", XmlFormula.Format(parsed));
        Assert.False(XmlFormula.TryParse("StringConcat($other, '-')", out _, out _, body));
        Assert.False(XmlFormula.TryParse("StringConcat($raw, /s:Order/s:Id)", out _, out _, body));
        Assert.False(XmlFormula.TryParse("StringConcat($raw, '-')", out _, out _, context));
    }

    [Fact]
    public void Validate_RejectsBadNamesDuplicatesAndBodiesReadingTheMessage()
    {
        Assert.Null(XmlCustomFunctions.Validate([Stub("A", "x"), Stub("B")]));
        Assert.NotNull(XmlCustomFunctions.Validate([Stub("A"), Stub("A")]));
        Assert.NotNull(XmlCustomFunctions.Validate([Stub("StringConcat")]));
        Assert.NotNull(XmlCustomFunctions.Validate([Stub("1bad")]));
        Assert.NotNull(XmlCustomFunctions.Validate([Stub("A", "x", "x")]));
        var reads = Stub("A", "x");
        reads.Body = new() { Kind = "Source", SourcePath = "/Order/Id" };
        Assert.NotNull(XmlCustomFunctions.Validate([reads]));
        var fine = Stub("A", "x");
        fine.Body = Parse("StringUpperCase($x)", XmlCustomFunctions.ContextForBody([fine], fine));
        Assert.Null(XmlCustomFunctions.Validate([fine]));
        Assert.False(XmlCustomFunctions.IsStub(fine));
        Assert.True(XmlCustomFunctions.IsStub(Stub()));
    }

    [Fact]
    public void BuildPrompt_ContainsTheStubDocumentationAndRulesButNoMessageData()
    {
        var helper = Stub("Trim2", "v");
        helper.Body = new() { Kind = "Function", Function = "StringTrim", Arguments = [new() { Kind = "Parameter", Name = "v" }] };
        var function = Stub("FormatDate", "raw");
        var prompt = XmlCustomFunctions.BuildPrompt(function, [function, helper]);
        Assert.Contains("FormatDate($raw)", prompt, StringComparison.Ordinal);
        Assert.Contains("yyyy-MM-dd", prompt, StringComparison.Ordinal);
        Assert.Contains("public string FormatDate", prompt, StringComparison.Ordinal);
        Assert.Contains("feeds Number", prompt, StringComparison.Ordinal);
        Assert.Contains("DateReformat", prompt, StringComparison.Ordinal);
        Assert.Contains("Trim2(v)", prompt, StringComparison.Ordinal);
        Assert.Contains(XmlCustomFunctions.CannotExpress, prompt, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("StringUpperCase($a)", "StringUpperCase($a)")]
    [InlineData("  StringUpperCase($a)\r\n", "StringUpperCase($a)")]
    [InlineData("```\nStringUpperCase($a)\n```", "StringUpperCase($a)")]
    [InlineData("Here you go:\n```text\nBody: StringUpperCase($a)\n```\nHope that helps", "StringUpperCase($a)")]
    [InlineData("Formula: StringUpperCase($a)", "StringUpperCase($a)")]
    [InlineData("`StringUpperCase($a)`", "StringUpperCase($a)")]
    public void ExtractFormula_ToleratesFencesAndPrefixes(string reply, string expected)
    {
        Assert.Equal(expected, XmlCustomFunctions.ExtractFormula(reply, out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CANNOT_EXPRESS: needs a database lookup")]
    public void ExtractFormula_ReportsEmptyRepliesAndRefusals(string reply)
    {
        Assert.Null(XmlCustomFunctions.ExtractFormula(reply, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void IsReferenced_SeesFieldAndFunctionCalls()
    {
        var map = MapWith(Stub("A", "x"), Stub("B", "x"));
        map.CustomFunctions[1].Body = new() { Kind = "Custom", Name = "A", Arguments = [new() { Kind = "Parameter", Name = "x" }] };
        Assert.True(XmlCustomFunctions.IsReferenced(map, "A"));
        Assert.False(XmlCustomFunctions.IsReferenced(map, "B"));
        map.Fields.Add(new() { TargetPath = "T", Expression = new() { Kind = "Custom", Name = "B", Arguments = [new() { Kind = "Constant", Constant = "1" }] } });
        Assert.True(XmlCustomFunctions.IsReferenced(map, "B"));
    }

    [Fact]
    public async Task Editor_AppliesAnAiReply_SavesItAndKeepsStubsFailClosedUntilThen()
    {
        var editor = new XmlCustomFunctionsEditor();
        var map = MapWith(Stub("FormatDate", "raw"));
        editor.Map = map;
        var changes = 0;
        editor.Changed = EventCallback.Factory.Create(this, () => { changes++; });

        Reply(editor, map.CustomFunctions[0]).GetType().GetProperty("Text")!.SetValue(Reply(editor, map.CustomFunctions[0]), "```\nDateReformat($raw, 'yyyyMMdd', 'yyyy-MM-dd')\n```");
        await (Task)Invoke(editor, "ApplyReplyAsync", map.CustomFunctions[0])!;
        Assert.Null(Field(editor, "_error").GetValue(editor));
        Assert.Equal(1, changes);
        Assert.NotNull(map.CustomFunctions[0].Body);
        Assert.Equal("DateReformat($raw, 'yyyyMMdd', 'yyyy-MM-dd')", XmlFormula.Format(map.CustomFunctions[0].Body!));

        await (Task)Invoke(editor, "ClearBodyAsync", map.CustomFunctions[0])!;
        Assert.Null(map.CustomFunctions[0].Body);
        Assert.Equal(2, changes);
    }

    [Theory]
    [InlineData("NoSuchFunction($raw)")]
    [InlineData("StringUpperCase($missing)")]
    [InlineData("StringUpperCase(/Order/Id)")]
    [InlineData("CANNOT_EXPRESS: too complex")]
    public async Task Editor_RejectsInvalidRepliesWithoutChangingTheStub(string reply)
    {
        var editor = new XmlCustomFunctionsEditor();
        var map = MapWith(Stub("FormatDate", "raw"));
        editor.Map = map;
        Reply(editor, map.CustomFunctions[0]).GetType().GetProperty("Text")!.SetValue(Reply(editor, map.CustomFunctions[0]), reply);
        await (Task)Invoke(editor, "ApplyReplyAsync", map.CustomFunctions[0])!;
        Assert.Null(map.CustomFunctions[0].Body);
        Assert.False(string.IsNullOrEmpty((string?)Field(editor, "_error").GetValue(editor)));
    }

    [Fact]
    public async Task Editor_AddsRenamesParametersAndBlocksRemovingACalledFunction()
    {
        var editor = new XmlCustomFunctionsEditor();
        var map = MapWith();
        editor.Map = map;
        Field(editor, "_newName").SetValue(editor, "Greet");
        await (Task)Invoke(editor, "AddAsync")!;
        var function = Assert.Single(map.CustomFunctions);
        Assert.Equal("Greet", function.Name);

        await (Task)Invoke(editor, "SetParametersAsync", function, "first, second")!;
        Assert.Equal(["first", "second"], function.Parameters.Select(parameter => parameter.Name));
        await (Task)Invoke(editor, "SetParametersAsync", function, "first, first")!;
        Assert.Equal(["first", "second"], function.Parameters.Select(parameter => parameter.Name));
        Assert.NotNull(Field(editor, "_error").GetValue(editor));

        Field(editor, "_newName").SetValue(editor, "Greet");
        await (Task)Invoke(editor, "AddAsync")!;
        Assert.Single(map.CustomFunctions);

        map.Fields.Add(new() { TargetPath = "T", Expression = new() { Kind = "Custom", Name = "Greet", Arguments = [new() { Kind = "Constant", Constant = "a" }, new() { Kind = "Constant", Constant = "b" }] } });
        await (Task)Invoke(editor, "RemoveAsync", function)!;
        Assert.Single(map.CustomFunctions);
        map.Fields.Clear();
        await (Task)Invoke(editor, "RemoveAsync", function)!;
        Assert.Empty(map.CustomFunctions);
    }

    [Fact]
    public async Task MappingEditor_RoundTripsStubsAndShowsThePendingBanner()
    {
        var json = JsonSerializer.Serialize(new XmlMappingDefinition
        {
            Version = 2, TargetRoot = "Out",
            Fields = [new()
            {
                TargetPath = "B",
                Expression = new()
                {
                    Kind = XmlValueExpressionKind.Custom, Name = "FormatDate",
                    Arguments = [new() { Kind = XmlValueExpressionKind.Source, SourcePath = "/In/A" }]
                }
            }],
            CustomFunctions =
            [
                new()
                {
                    Name = "FormatDate", Description = "Imported stub", OriginalSource = "public string FormatDate(string raw) { return raw; }", OriginalSourceLanguage = "CSharp",
                    Parameters = [new() { Name = "raw", Description = "string" }], Usages = ["Map 'm', functoid 10"]
                }
            ]
        }, Web);
        var editor = new XmlMappingEditor();
        typeof(XmlMappingEditor).GetProperty(nameof(XmlMappingEditor.Value))!.SetValue(editor, json);
        typeof(XmlMappingEditor).GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
        var map = Assert.IsType<XmlMappingViewModel>(typeof(XmlMappingEditor).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        Assert.True(XmlCustomFunctions.IsStub(Assert.Single(map.CustomFunctions)));
        Assert.Equal("FormatDate(/In/A)", XmlFormula.Format(map.Fields[0].Expression!));

        using var http = new HttpClient(new NotFound()) { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http))
            .AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<XmlMappingEditor>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(XmlMappingEditor.Value)] = json }))).ToHtmlString());
        Assert.DoesNotContain("original definition is preserved", html);
        Assert.Contains("Custom functions (1, 1 stub(s) to implement)", html);
        Assert.Contains("stub (not implemented)", html);
        Assert.Contains("FormatDate($raw)", html);
        Assert.Contains("Imported stub", html);
        Assert.Contains("never run", html);
        Assert.Contains("AI prompt for FormatDate", html);
        Assert.Contains("FormatDate(/In/A)", html);
    }

    [Theory]
    [InlineData("{\"version\":2,\"targetRoot\":\"Out\",\"fields\":[],\"customFunctions\":[{\"name\":\"StringConcat\"}]}")]
    [InlineData("{\"version\":1,\"targetRoot\":\"Out\",\"fields\":[],\"customFunctions\":[{\"name\":\"A\"}]}")]
    [InlineData("{\"version\":2,\"targetRoot\":\"Out\",\"fields\":[{\"targetPath\":\"B\",\"expression\":{\"kind\":\"Custom\",\"name\":\"Missing\",\"arguments\":[]}}]}")]
    [InlineData("{\"version\":2,\"targetRoot\":\"Out\",\"fields\":[],\"customFunctions\":null}")]
    public void InvalidCustomFunctionJson_IsPreservedWithAnError(string json)
    {
        var editor = new XmlMappingEditor();
        typeof(XmlMappingEditor).GetProperty(nameof(XmlMappingEditor.Value))!.SetValue(editor, json);
        typeof(XmlMappingEditor).GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
        Assert.Null(typeof(XmlMappingEditor).GetField("_map", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor));
        Assert.Equal(json, editor.Value);
    }

    private static object Reply(object editor, XmlCustomFunctionViewModel function) => Invoke(editor, "Reply", function)!;
    private static FieldInfo Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);

    private sealed class NotFound : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
}
