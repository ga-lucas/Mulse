using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mulse.Excel;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Service.Models;

namespace Service.Tests;

public sealed class FileDraftTests
{
    private static FlowDesignService Analyzer() => new(FakeModuleCatalog.WithModules(
        ("file-system-fetch", ModuleKind.Fetch), ("excel-parse", ModuleKind.Parse), ("json-parse", ModuleKind.Parse),
        ("excel-render", ModuleKind.Render), ("json-render", ModuleKind.Render), ("logging-deliver", ModuleKind.Deliver)), new FakeFlowDefinitionService());

    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public async Task WorkbookSample_DetectsBinaryAndRecommendsExcelWithoutExecutingParser(string format)
    {
        var batch = new IntegrationBatch([new("rows.json", BinaryData.FromString("[{\"id\":1}]"), "application/json", new Dictionary<string, string>())]);
        var workbook = await new ExcelRenderModule().RenderAsync(new("test", "run", DateTimeOffset.UtcNow), batch,
            new ModuleStepDefinition { Settings = new() { ["format"] = format } }, CancellationToken.None);
        var request = new FlowDesignAnalysisRequestViewModel("orders." + format, "application/octet-stream", "",
            Convert.ToBase64String(workbook.Payloads.Single().Content.ToArray()));
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        var serverRequest = JsonSerializer.Deserialize<AnalyzeFlowDesignRequest>(JsonSerializer.Serialize(request, options), options)!;
        var result = await Analyzer().AnalyzeAsync(serverRequest, CancellationToken.None);
        Assert.Equal(DetectedDataFormat.Excel, result.DetectedFormat);
        Assert.Equal("excel-parse", result.ParseModules[0].Id);
        Assert.True(result.ParseModules[0].IsRecommended);
        Assert.False(result.ParseModules.Single(module => module.Id == "json-parse").IsRecommended);
        Assert.Empty(result.Fields);
        Assert.Contains("sheetName", result.Preview);
        var response = JsonSerializer.Deserialize<FlowDesignAnalysisViewModel>(JsonSerializer.Serialize(result, options), options)!;
        Assert.Equal("Excel", response.DetectedFormat);
        var designer = new Designer();
        AddSource(designer, response);
        Assert.Contains(Nodes(designer), node => Property<string>(node, "ModuleId") == "excel-parse");
    }

    [Theory]
    [InlineData("bad.xlsx", "not a workbook")]
    [InlineData("bad.xls", "not a workbook")]
    [InlineData("archive.zip", "anything")]
    public async Task InvalidBinarySamples_FailExplicitly(string filename, string content)
        => await Assert.ThrowsAsync<ArgumentException>(() => Analyzer().AnalyzeAsync(new AnalyzeFlowDesignRequest
        {
            FileName = filename, TextContent = "", BinaryContent = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content))
        }, CancellationToken.None));

    [Fact]
    public async Task BinarySampleValidation_RejectsMalformedBase64OversizeAndNonText()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Analyzer().AnalyzeAsync(new AnalyzeFlowDesignRequest
        { FileName = "bad.xls", TextContent = "", BinaryContent = "not base64" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Analyzer().AnalyzeAsync(new AnalyzeFlowDesignRequest
        { FileName = "big.xls", TextContent = "", BinaryContent = new string('A', 14_000_000) }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => Analyzer().AnalyzeAsync(new AnalyzeFlowDesignRequest
        { FileName = "binary.bin", TextContent = "\0\u0001\u0002" }, CancellationToken.None));
    }

    [Fact]
    public void AdditiveDraft_PreservesExistingNodesPositionsSettingsAndEnabledState()
    {
        var designer = new Designer();
        var modules = new[]
        {
            new ModuleViewModel("file-system-fetch", "Files", "Fetch", "", [], [], ["FileSystem"], []),
            new ModuleViewModel("excel-parse", "Excel", "Parse", "", [], [], [], ["Parsing"])
        };
        Field("_modules").SetValue(designer, modules);
        var excel = Analysis("Excel", "orders.xlsx");
        AddSource(designer, excel);
        var original = Nodes(designer);
        Assert.Equal(4, original.Length);
        Assert.False((bool)Field("_enabled").GetValue(designer)!);
        Assert.Equal("OnDemand", Field("_triggerMode").GetValue(designer));
        var fetch = original.First(node => Property<string>(node, "GraphId").EndsWith("-fetch", StringComparison.Ordinal));
        Property<Dictionary<string, string>>(fetch, "Settings")["path"] = "C:\\input";
        fetch.GetType().GetProperty("X")!.SetValue(fetch, 713d);
        Field("_enabled").SetValue(designer, true);
        AddSource(designer, Analysis("Excel", "second.xls"));
        Assert.Equal(6, Nodes(designer).Length);
        Assert.All(original, node => Assert.Contains(node, Nodes(designer)));
        Assert.Equal(713d, Property<double>(fetch, "X"));
        Assert.Equal("C:\\input", Property<Dictionary<string, string>>(fetch, "Settings")["path"]);
        Assert.True((bool)Field("_enabled").GetValue(designer)!);
        var parses = Nodes(designer).Where(node => Property<string>(node, "ModuleId") == "excel-parse").ToArray();
        Assert.Equal(2, parses.Length);
        Assert.Equal(2, parses.Select(node => Property<string>(node, "GraphId")).Distinct().Count());
        Assert.All(parses, node => Assert.Equal("1", Property<Dictionary<string, string>>(node, "Settings")["sheetNumber"]));
        var secondFetch = Nodes(designer).Last(node => Property<string>(node, "GraphId").EndsWith("-fetch", StringComparison.Ordinal));
        Assert.Equal(string.Empty, Property<string>(secondFetch, "ModuleId"));
        Assert.Empty(Property<Dictionary<string, string>>(secondFetch, "Settings"));
    }

    [Fact]
    public void ApplyingSampleAnalysis_AddsLaneWithoutReplacingNodesOrChoosingDataSource()
    {
        var designer = new Designer();
        Field("_modules").SetValue(designer, new[]
        {
            new ModuleViewModel("excel-parse", "Excel", "Parse", "", [], [], [], ["Parsing"])
        });
        AddSource(designer, Analysis("Excel", "first.xlsx"));
        Assert.Equal("excel-workflow", Field("_flowId").GetValue(designer));
        var originalNodes = Nodes(designer);
        Field("_analysis").SetValue(designer, Analysis("Excel", "second.xlsx"));
        Field("_showSamplePanel").SetValue(designer, true);

        typeof(Designer).GetMethod("ApplySampleAnalysis", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(designer, null);

        var nodes = Nodes(designer);
        Assert.Equal(6, nodes.Length);
        Assert.All(originalNodes, node => Assert.Contains(node, nodes));
        var newFetch = nodes.Last(node => Property<string>(node, "GraphId").EndsWith("-fetch", StringComparison.Ordinal));
        Assert.Equal(string.Empty, Property<string>(newFetch, "ModuleId"));
        Assert.Equal("Choose the real data source", Property<string?>(newFetch, "Name"));
        Assert.Empty(Property<Dictionary<string, string>>(newFetch, "Settings"));
        Assert.False((bool)Field("_showSamplePanel").GetValue(designer)!);
    }

    [Theory]
    [InlineData("Json", "json-parse")]
    [InlineData("Xml", "xml-parse")]
    [InlineData("Csv", "csv-parse")]
    [InlineData("Text", "text-parse")]
    public void FileDraft_SelectsCompatibleParserRatherThanFirstSuggestion(string format, string parser)
    {
        var designer = new Designer();
        var analysis = Analysis(format, "sample.dat") with
        {
            ParseModules = [
                new("wrong-parser", "Wrong", "Parse", "", true, [], ["Unsupported"], [], ["Parsing"]),
                new(parser, "Compatible", "Parse", "", true, [], [format], [], ["Parsing"])
            ]
        };
        AddSource(designer, analysis);
        Assert.Contains(Nodes(designer), node => Property<string>(node, "ModuleId") == parser);
        Assert.DoesNotContain(Nodes(designer), node => Property<string>(node, "ModuleId") == "wrong-parser");
    }

    [Fact]
    public void MissingExcelPackage_KeepsExplicitExcelNodeAndActionableWarning()
    {
        var designer = new Designer();
        AddSource(designer, Analysis("Excel", "orders.xlsx"));
        Assert.Contains(Nodes(designer), node => Property<string>(node, "ModuleId") == "excel-parse");
        Assert.Contains("Load Mulse.Excel", (string)Field("_errorMessage").GetValue(designer)!);
    }

    private static FlowDesignAnalysisViewModel Analysis(string format, string file) => new(format, file, "preview", [], [], [], [], [], []);
    private static FieldInfo Field(string name) => typeof(Designer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static object[] Nodes(Designer designer) => ((IEnumerable)Field("_nodes").GetValue(designer)!).Cast<object>().ToArray();
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static void AddSource(Designer designer, FlowDesignAnalysisViewModel analysis)
        => typeof(Designer).GetMethod("AddFileSource", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(designer, [analysis]);
}
