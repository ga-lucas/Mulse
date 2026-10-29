using System.Globalization;
using System.Text.Json;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Modules.Sdk;
using NPOI.SS.UserModel;

namespace Mulse.Excel;

public sealed class ExcelParseModule : IParseModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "excel-parse", "Excel parse", ModuleKind.Parse,
        "Reads a named or numbered worksheet from an XLS or XLSX workbook into JSON rows.",
        [.. ExcelSettings.SheetDescriptors,
            new("maxRows", "Maximum data rows", "Reject worksheets exceeding this many data rows (1-1048576).", false, ModuleSettingInputKind.Number, "100000")],
        new ModuleRecommendationProfile(capabilities: [ModuleCapability.Parsing, ModuleCapability.Transformation]));

    public Task<IntegrationBatch> ParseAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (name, number, headers) = ExcelSettings.Read(step, Descriptor.Id);
        var maxRows = ModuleSettingReader.GetInt32(step.Settings, "maxRows", 100000, Descriptor.Id);
        if (maxRows is < 1 or > 1048576) throw new ArgumentException("excel-parse requires maxRows between 1 and 1048576.");
        var payloads = new List<IntegrationPayload>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = payload.Content.ToStream();
            using var workbook = WorkbookFactory.Create(input);
            var sheet = ExcelSettings.SelectSheet(workbook, name, number);
            var rows = new List<object>();
            var first = sheet.FirstRowNum;
            var last = sheet.PhysicalNumberOfRows == 0 ? first - 1 : sheet.LastRowNum;
            var start = headers ? first + 1 : first;
            if (last - start + 1 > maxRows)
                throw new InvalidOperationException($"Excel sheet '{sheet.SheetName}' exceeds maxRows ({maxRows}).");
            var width = 0;
            for (var index = first; index <= last; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                width = Math.Max(width, sheet.GetRow(index)?.LastCellNum ?? 0);
            }
            var columns = headers ? ReadHeaders(sheet, first, width) : [];
            for (var index = start; index <= last; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = sheet.GetRow(index);
                var values = Enumerable.Range(0, width).Select(column => ExcelSettings.CellValue(row?.GetCell(column), sheet.SheetName)).ToArray();
                if (headers)
                    rows.Add(columns.Select((column, i) => (column, value: values[i])).ToDictionary(item => item.column, item => item.value));
                else
                    rows.Add(values);
            }
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["parsedBy"] = Descriptor.Id, ["parsedFormat"] = "Excel", ["excelSheetName"] = sheet.SheetName,
                ["excelSheetNumber"] = (workbook.GetSheetIndex(sheet) + 1).ToString(CultureInfo.InvariantCulture)
            };
            payloads.Add(new IntegrationPayload(Path.ChangeExtension(payload.Name, ".json"),
                BinaryData.FromBytes(JsonSerializer.SerializeToUtf8Bytes(rows)), "application/json", metadata));
        }
        return Task.FromResult(new IntegrationBatch(payloads));
    }

    private static string[] ReadHeaders(ISheet sheet, int first, int width)
    {
        var columns = new string[width];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var column = 0; column < width; column++)
        {
            var value = ExcelSettings.CellValue(sheet.GetRow(first)?.GetCell(column), sheet.SheetName);
            var header = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(header) || !seen.Add(header))
                throw new ArgumentException($"Excel sheet '{sheet.SheetName}' has a blank or duplicate header at column {column + 1}. Use unique headers or disable firstRowIsHeader.");
            columns[column] = header;
        }
        return columns;
    }
}
