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
    public string TransformModule { get; set; } = string.Empty;
    public Dictionary<string, string> TransformSettings { get; set; } = [];
    public List<ParsedDataTransformInputViewModel> TransformInputs { get; set; } = [];
    public string SourceScope { get; set; } = "Current";
    public List<ParsedDataFieldViewModel> Fields { get; set; } = [];
    public List<string> InputFields { get; set; } = [];
    public List<string> OutputFields { get; set; } = [];
    public List<ParsedDataConditionViewModel> Conditions { get; set; } = [];
    public string ConditionMatch { get; set; } = "All";
    public string Otherwise { get; set; } = "Omit";
    public string OtherwiseJson { get; set; } = "null";
    public List<ParsedDataTransformStepViewModel> TransformChain { get; set; } = [];
}

public sealed class ParsedDataTransformInputViewModel
{
    public string Kind { get; set; } = "Field";
    public string SourcePath { get; set; } = string.Empty;
    public string ConstantJson { get; set; } = "null";
    public string SourceScope { get; set; } = "Current";
}

public sealed class ParsedDataTransformStepViewModel
{
    public string Module { get; set; } = string.Empty;
    public Dictionary<string, string> Settings { get; set; } = [];
    public List<ParsedDataTransformInputViewModel> Inputs { get; set; } = [new() { Kind = "Previous" }];
}

public sealed class ParsedDataConditionViewModel
{
    public string SourcePath { get; set; } = string.Empty;
    public string SourceScope { get; set; } = "Current";
    public string Operator { get; set; } = "Exists";
    public string ValueJson { get; set; } = "null";
}

public sealed record ParsedMappingSourceViewModel(string Id, string Module, Dictionary<string, string> Settings);
public sealed record ParsedDataSampleRequestViewModel(string Text, string Format = "Auto", string RecordPath = "", string? ParserModule = null,
    Dictionary<string, string>? ParserSettings = null, string? BinaryContent = null, string? FileName = null, string? ContentType = null);
public sealed record ParsedDataInspectionViewModel(string ParsedData, IReadOnlyList<string> Fields, int RecordCount, IReadOnlyList<string>? Samples = null);
public sealed record ParsedDataPreviewRequestViewModel(string MappingJson, string ParsedData, string Format = "Auto");
public sealed record ParsedDataPreviewViewModel(string OutputJson);
