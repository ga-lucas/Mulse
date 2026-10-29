using System.Text.Json;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Modules.Sdk;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace Mulse.Excel;

public sealed class ExcelRenderModule : IRenderModule
{
    public ModuleDescriptor Descriptor { get; } = new(
        "excel-render", "Excel render", ModuleKind.Render,
        "Writes JSON arrays of objects or cell arrays to a new XLS or XLSX workbook. Strings are written literally, never as formulas.",
        [.. ExcelSettings.SheetDescriptors,
            new("format", "Workbook format", "Output workbook format.", false, ModuleSettingInputKind.Select, "xlsx",
                [new("xlsx", "Excel workbook (.xlsx)"), new("xls", "Excel 97-2003 workbook (.xls)")])],
        new ModuleRecommendationProfile([ModuleDataFormat.Json], capabilities: [ModuleCapability.Serialization, ModuleCapability.Transformation]));

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (name, number, headers) = ExcelSettings.Read(step, Descriptor.Id);
        var format = ModuleSettingReader.GetOptional(step.Settings, "format")?.ToLowerInvariant() ?? "xlsx";
        if (format is not ("xls" or "xlsx")) throw new ArgumentException("excel-render format must be xls or xlsx.");
        var rowLimit = format == "xls" ? 65536 : 1048576;
        var columnLimit = format == "xls" ? 256 : 16384;
        var payloads = new List<IntegrationPayload>();
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(payload.Content);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("excel-render requires a JSON array of objects or arrays.");
            var rows = document.RootElement;
            var objectRows = rows.GetArrayLength() > 0 && rows[0].ValueKind == JsonValueKind.Object;
            var columns = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (row.ValueKind != (objectRows ? JsonValueKind.Object : JsonValueKind.Array))
                    throw new ArgumentException("excel-render rows must be all objects or all arrays.");
                if (objectRows)
                {
                    var rowNames = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var property in row.EnumerateObject())
                    {
                        if (string.IsNullOrWhiteSpace(property.Name) || !rowNames.Add(property.Name))
                            throw new ArgumentException("excel-render object rows require nonblank, unique property names.");
                        if (seen.Add(property.Name)) columns.Add(property.Name);
                    }
                }
                else if (row.GetArrayLength() > columnLimit)
                    throw new ArgumentException($"Excel {format} supports at most {columnLimit} columns.");
            }
            var writeHeaders = objectRows && headers;
            if (rows.GetArrayLength() + (writeHeaders ? 1L : 0L) > rowLimit || columns.Count > columnLimit)
                throw new ArgumentException($"Excel {format} supports at most {rowLimit} rows and {columnLimit} columns, including headers.");
            using IWorkbook workbook = format == "xls" ? new HSSFWorkbook() : new XSSFWorkbook();
            ISheet sheet;
            if (name is not null)
                sheet = workbook.CreateSheet(name);
            else
            {
                for (var index = 1; index < number; index++) workbook.CreateSheet($"Sheet{index}");
                sheet = workbook.CreateSheet($"Sheet{number}");
            }
            var rowIndex = 0;
            if (writeHeaders)
            {
                var header = sheet.CreateRow(rowIndex++);
                for (var column = 0; column < columns.Count; column++) header.CreateCell(column).SetCellValue(columns[column]);
            }
            foreach (var row in rows.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = sheet.CreateRow(rowIndex++);
                if (objectRows)
                {
                    for (var column = 0; column < columns.Count; column++)
                        if (row.TryGetProperty(columns[column], out var value)) WriteCell(target.CreateCell(column), value);
                }
                else
                {
                    var column = 0;
                    foreach (var value in row.EnumerateArray()) WriteCell(target.CreateCell(column++), value);
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            using var output = new MemoryStream();
            workbook.Write(output, leaveOpen: true);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id, ["renderedFormat"] = "Excel", ["excelSheetName"] = sheet.SheetName,
                ["excelSheetNumber"] = number.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
            payloads.Add(new IntegrationPayload(Path.ChangeExtension(payload.Name, "." + format), BinaryData.FromBytes(output.ToArray()),
                format == "xls" ? "application/vnd.ms-excel" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", metadata));
        }
        return Task.FromResult(new IntegrationBatch(payloads));
    }

    private static void WriteCell(ICell cell, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null: break;
            case JsonValueKind.String: cell.SetCellValue(value.GetString()); break;
            case JsonValueKind.True: case JsonValueKind.False: cell.SetCellValue(value.GetBoolean()); break;
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
                    throw new ArgumentException("Excel numeric cells require a finite double-precision number. Write precision-sensitive identifiers as strings.");
                cell.SetCellValue(number);
                break;
            default: throw new ArgumentException("excel-render cells must be strings, numbers, booleans, or null; nested objects/arrays are not supported.");
        }
    }
}
