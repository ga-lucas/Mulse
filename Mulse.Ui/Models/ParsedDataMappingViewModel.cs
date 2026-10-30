namespace Mulse.Ui.Models;

public sealed class ParsedDataMapViewModel
{
    public int Version { get; set; } = 1;
    public string RecordPath { get; set; } = string.Empty;
    public List<string> InputFields { get; set; } = [];
    public List<string> OutputFields { get; set; } = [];
    public List<ParsedDataFieldViewModel> Fields { get; set; } = [];
}

public sealed class ParsedDataFieldViewModel
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string Kind { get; set; } = "Field";
    public string ConstantJson { get; set; } = "null";
    public string Missing { get; set; } = "Error";
}

public sealed record ParsedMappingSourceViewModel(string Id, string Module, Dictionary<string, string> Settings);
public sealed record ParsedDataSampleRequestViewModel(string Text, string Format = "Auto", string RecordPath = "", string? ParserModule = null,
    Dictionary<string, string>? ParserSettings = null, string? BinaryContent = null, string? FileName = null, string? ContentType = null);
public sealed record ParsedDataInspectionViewModel(string ParsedData, IReadOnlyList<string> Fields, int RecordCount, IReadOnlyList<string>? Samples = null);
public sealed record ParsedDataPreviewRequestViewModel(string MappingJson, string ParsedData, string Format = "Auto");
public sealed record ParsedDataPreviewViewModel(string OutputJson);
