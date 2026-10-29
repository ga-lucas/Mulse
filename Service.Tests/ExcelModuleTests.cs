using System.Text.Json;
using Mulse.Excel;
using Mulse.Modules.Abstractions;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Service.Tests;

public sealed class ExcelModuleTests
{
    private static FlowExecutionContext Context() => new("excel-flow", "test", DateTimeOffset.UtcNow);
    private static IntegrationBatch JsonBatch(string json) => new([new IntegrationPayload("orders.json", BinaryData.FromString(json),
        "application/json", new Dictionary<string, string> { ["correlationId"] = "123" })]);
    private static ModuleStepDefinition Step(params (string Key, string Value)[] settings) => new()
    {
        Settings = settings.ToDictionary(item => item.Key, item => item.Value)
    };

    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public async Task NamedSheet_RoundTripsObjectsAndPreservesTypesMetadataAndLiteralStrings(string format)
    {
        var input = JsonBatch("""[{"Order":"00123","Amount":12.5,"Active":true,"Note":"=SUM(A1:A2)","Optional":null},{"Order":"two","Amount":-3,"Active":false,"Extra":"later column"}]""");
        var step = Step(("format", format), ("sheetName", "Orders"));
        var rendered = Assert.Single((await new ExcelRenderModule().RenderAsync(Context(), input, step, CancellationToken.None)).Payloads);
        Assert.Equal("orders." + format, rendered.Name);
        Assert.Equal(format == "xls" ? "application/vnd.ms-excel" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", rendered.ContentType);
        Assert.Equal("123", rendered.Metadata["correlationId"]);
        using (var stream = rendered.Content.ToStream())
        using (var workbook = WorkbookFactory.Create(stream))
        {
            Assert.Equal(CellType.String, workbook.GetSheet("Orders").GetRow(1).GetCell(3).CellType);
            Assert.Equal("=SUM(A1:A2)", workbook.GetSheet("Orders").GetRow(1).GetCell(3).StringCellValue);
        }
        var parsed = Assert.Single((await new ExcelParseModule().ParseAsync(Context(), new([rendered]), step, CancellationToken.None)).Payloads);
        using var rows = JsonDocument.Parse(parsed.Content);
        Assert.Equal(2, rows.RootElement.GetArrayLength());
        var first = rows.RootElement[0];
        Assert.Equal("00123", first.GetProperty("Order").GetString());
        Assert.Equal(12.5, first.GetProperty("Amount").GetDouble());
        Assert.True(first.GetProperty("Active").GetBoolean());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("Optional").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("Extra").ValueKind);
        Assert.Equal("later column", rows.RootElement[1].GetProperty("Extra").GetString());
        Assert.Equal("Orders", parsed.Metadata["excelSheetName"]);
        Assert.Equal("1", parsed.Metadata["excelSheetNumber"]);
        Assert.Equal("123", parsed.Metadata["correlationId"]);
        Assert.Equal("application/json", parsed.ContentType);
        Assert.Equal("orders.json", parsed.Name);
    }

    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public async Task SheetNumberAndHeaderlessRows_RoundTripWithBlankCells(string format)
    {
        var step = Step(("format", format), ("sheetNumber", "3"), ("firstRowIsHeader", "false"));
        var rendered = await new ExcelRenderModule().RenderAsync(Context(), JsonBatch("""[["id",null,12,true],["next"],[]]"""), step, CancellationToken.None);
        var parsed = await new ExcelParseModule().ParseAsync(Context(), rendered, step, CancellationToken.None);
        var payload = Assert.Single(parsed.Payloads);
        Assert.Equal("3", payload.Metadata["excelSheetNumber"]);
        Assert.Equal("Sheet3", payload.Metadata["excelSheetName"]);
        using var rows = JsonDocument.Parse(payload.Content);
        Assert.Equal(3, rows.RootElement.GetArrayLength());
        Assert.Equal("id", rows.RootElement[0][0].GetString());
        Assert.Equal(JsonValueKind.Null, rows.RootElement[0][1].ValueKind);
        Assert.Equal(12, rows.RootElement[0][2].GetInt32());
        Assert.Equal(4, rows.RootElement[1].GetArrayLength());
        Assert.Equal(JsonValueKind.Null, rows.RootElement[2][3].ValueKind);
        var named = await new ExcelParseModule().ParseAsync(Context(), rendered,
            Step(("sheetName", "Sheet3"), ("sheetNumber", "invalid-but-name-wins"), ("firstRowIsHeader", "false")), CancellationToken.None);
        Assert.Equal(payload.Content.ToString(), named.Payloads.Single().Content.ToString());
    }

    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public async Task ReadingDatesAndCachedFormulas_UsesTypedValuesAndDoesNotEvaluate(string format)
    {
        using IWorkbook workbook = format == "xls" ? new HSSFWorkbook() : new XSSFWorkbook();
        var sheet = workbook.CreateSheet("Data");
        var row = sheet.CreateRow(0);
        var date = row.CreateCell(0);
        date.SetCellValue(new DateTime(2026, 10, 29));
        var style = workbook.CreateCellStyle();
        style.DataFormat = workbook.CreateDataFormat().GetFormat("yyyy-mm-dd");
        date.CellStyle = style;
        row.CreateCell(1).SetCellValue(7);
        var formula = row.CreateCell(2);
        formula.SetCellFormula("B1*2");
        workbook.GetCreationHelper().CreateFormulaEvaluator().EvaluateFormulaCell(formula);
        using var output = new MemoryStream();
        workbook.Write(output, leaveOpen: true);
        var batch = new IntegrationBatch([new("input." + format, BinaryData.FromBytes(output.ToArray()), "application/octet-stream", new Dictionary<string, string>())]);
        var parsed = await new ExcelParseModule().ParseAsync(Context(), batch, Step(("firstRowIsHeader", "false")), CancellationToken.None);
        using var json = JsonDocument.Parse(parsed.Payloads.Single().Content);
        Assert.StartsWith("2026-10-29T00:00:00", json.RootElement[0][0].GetString());
        Assert.Equal(14, json.RootElement[0][2].GetInt32());
    }

    [Theory]
    [InlineData("[{\"A\":1},{\"A\":2}]", "maxRows", "1")]
    [InlineData("[{\"A\":1}]", "sheetName", "Missing")]
    [InlineData("[{\"A\":1}]", "sheetNumber", "2")]
    [InlineData("[{\"A\":1}]", "sheetNumber", "0")]
    public async Task ReadingInvalidSelectionOrRowLimit_FailsExplicitly(string json, string key, string value)
    {
        var workbook = await new ExcelRenderModule().RenderAsync(Context(), JsonBatch(json), Step(), CancellationToken.None);
        var parse = new ExcelParseModule();
        if (key == "maxRows")
            await Assert.ThrowsAsync<InvalidOperationException>(() => parse.ParseAsync(Context(), workbook, Step((key, value)), CancellationToken.None));
        else
            await Assert.ThrowsAsync<ArgumentException>(() => parse.ParseAsync(Context(), workbook, Step((key, value)), CancellationToken.None));
    }

    [Theory]
    [InlineData("[[\"A\",\"A\"],[1,2]]")]
    [InlineData("[[\"A\",null],[1,2]]")]
    public async Task ReadingDuplicateOrBlankHeaders_FailsRatherThanLosingColumns(string json)
    {
        var workbook = await new ExcelRenderModule().RenderAsync(Context(), JsonBatch(json), Step(), CancellationToken.None);
        var error = await Assert.ThrowsAsync<ArgumentException>(() => new ExcelParseModule().ParseAsync(Context(), workbook, Step(), CancellationToken.None));
        Assert.Contains("blank or duplicate header", error.Message);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[1]")]
    [InlineData("[{\"A\":{} }]")]
    [InlineData("[{\"A\":1},[]]")]
    [InlineData("[{\"A\":1,\"A\":2}]")]
    public async Task WritingUnsupportedJson_FailsExplicitly(string json)
        => await Assert.ThrowsAsync<ArgumentException>(() => new ExcelRenderModule().RenderAsync(Context(), JsonBatch(json), Step(), CancellationToken.None));

    [Fact]
    public async Task WritingXlsLimitsAndInvalidSettings_FailsExplicitly()
    {
        var wide = "[" + JsonSerializer.Serialize(Enumerable.Range(0, 257)) + "]";
        await Assert.ThrowsAsync<ArgumentException>(() => new ExcelRenderModule().RenderAsync(Context(), JsonBatch(wide), Step(("format", "xls")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => new ExcelRenderModule().RenderAsync(Context(), JsonBatch("[]"), Step(("format", "csv")), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => new ExcelRenderModule().RenderAsync(Context(), JsonBatch("[]"), Step(("sheetName", "bad/name")), CancellationToken.None));
        var tooMany = JsonSerializer.Serialize(Enumerable.Range(0, 65536).Select(index => new { Value = index }));
        await Assert.ThrowsAsync<ArgumentException>(() => new ExcelRenderModule().RenderAsync(Context(), JsonBatch(tooMany), Step(("format", "xls")), CancellationToken.None));
    }

    [Fact]
    public async Task CancelledOperations_DoNotProcessPayloads()
    {
        var token = new CancellationToken(canceled: true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExcelRenderModule().RenderAsync(Context(), JsonBatch("[]"), Step(), token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ExcelParseModule().ParseAsync(Context(), JsonBatch("bad"), Step(), token));
    }

    [Theory]
    [InlineData("xls")]
    [InlineData("xlsx")]
    public async Task EmptyWorkbook_RoundTripsAsEmptyArray(string format)
    {
        var rendered = await new ExcelRenderModule().RenderAsync(Context(), JsonBatch("[]"), Step(("format", format)), CancellationToken.None);
        var parsed = await new ExcelParseModule().ParseAsync(Context(), rendered, Step(), CancellationToken.None);
        Assert.Equal("[]", parsed.Payloads.Single().Content.ToString());
    }
}
