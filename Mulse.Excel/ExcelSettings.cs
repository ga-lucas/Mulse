using System.Globalization;
using Mulse.Modules.Abstractions;
using Mulse.Modules.Pipeline;
using Mulse.Modules.Sdk;
using NPOI.SS.UserModel;

namespace Mulse.Excel;

internal static class ExcelSettings
{
    internal static readonly ModuleSettingDescriptor[] SheetDescriptors =
    [
        new("sheetName", "Sheet name", "Select a worksheet by name. Leave blank to use sheetNumber. For writing, names the output sheet in a new workbook.", false),
        new("sheetNumber", "Sheet number", "1-based worksheet number (1-256) when sheetName is blank. Writing creates preceding empty sheets if needed.", false, ModuleSettingInputKind.Number, "1"),
        new("firstRowIsHeader", "First row contains headers", "Read column names from the first row; write object property names as headers. Without headers, reading produces arrays of cell values.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    internal static (string? Name, int Number, bool Headers) Read(ModuleStepDefinition step, string moduleId)
    {
        var name = ModuleSettingReader.GetOptional(step.Settings, "sheetName");
        var number = name is null ? ModuleSettingReader.GetInt32(step.Settings, "sheetNumber", 1, moduleId) : 1;
        if (number is < 1 or > 256)
            throw new ArgumentException($"Module '{moduleId}' requires sheetNumber between 1 and 256.");
        return (name, number, ModuleSettingReader.GetBoolean(step.Settings, "firstRowIsHeader", true, moduleId));
    }

    internal static ISheet SelectSheet(IWorkbook workbook, string? name, int number)
    {
        if (name is not null)
            return workbook.GetSheet(name) ?? throw new ArgumentException($"Excel worksheet '{name}' was not found.");
        if (number > workbook.NumberOfSheets)
            throw new ArgumentException($"Excel sheetNumber {number} is outside this workbook's {workbook.NumberOfSheets} worksheet(s).");
        return workbook.GetSheetAt(number - 1);
    }

    internal static object? CellValue(ICell? cell, string sheetName)
    {
        if (cell is null) return null;
        var type = cell.CellType == CellType.Formula ? cell.CachedFormulaResultType : cell.CellType;
        return type switch
        {
            CellType.Blank => null,
            CellType.String => cell.StringCellValue,
            CellType.Boolean => cell.BooleanCellValue,
            CellType.Numeric => DateUtil.IsCellDateFormatted(cell)
                ? cell.DateCellValue?.ToString("O", CultureInfo.InvariantCulture)
                : cell.NumericCellValue,
            CellType.Error => throw new InvalidOperationException($"Excel cell error in sheet '{sheetName}', row {cell.RowIndex + 1}, column {cell.ColumnIndex + 1} (code {cell.ErrorCellValue})."),
            _ => throw new InvalidOperationException($"Unsupported Excel cell type in sheet '{sheetName}', row {cell.RowIndex + 1}, column {cell.ColumnIndex + 1}.")
        };
    }
}
