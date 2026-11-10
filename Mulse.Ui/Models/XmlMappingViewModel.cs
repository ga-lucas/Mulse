namespace Mulse.Ui.Models;

public sealed class XmlMappingViewModel
{
    public int Version { get; set; } = 1;
    public string? SourceRoot { get; set; }
    public string TargetRoot { get; set; } = "Output";
    public Dictionary<string, string> Namespaces { get; set; } = new(StringComparer.Ordinal);
    public List<XmlFieldMappingViewModel> Fields { get; set; } = [];
    public List<XmlRepeatMappingViewModel> Repeats { get; set; } = [];
    public List<XmlCustomFunctionViewModel> CustomFunctions { get; set; } = [];
    public List<string> SourceSchemas { get; set; } = [];
    public List<string> TargetSchemas { get; set; } = [];
    public bool ValidateSource { get; set; } = true;
    public bool ValidateTarget { get; set; } = true;
}

/// <summary>A reusable formula. Without a Body it is a stub (for example an imported BizTalk script): the map fails closed until it is implemented.</summary>
public sealed class XmlCustomFunctionViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<XmlCustomFunctionParameterViewModel> Parameters { get; set; } = [];
    public XmlValueExpressionViewModel? Body { get; set; }
    public string? OriginalSource { get; set; }
    public string? OriginalSourceLanguage { get; set; }
    public List<string> Usages { get; set; } = [];
}

public sealed class XmlCustomFunctionParameterViewModel
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class XmlRepeatMappingViewModel
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
}

public sealed class XmlFieldMappingViewModel
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string? Constant { get; set; }
    public XmlValueExpressionViewModel? Expression { get; set; }
    /// <summary>Optional (formula fields only): the target is written only when this formula yields the text 'true'. Imported BizTalk Value Mapping functoids become this.</summary>
    public XmlValueExpressionViewModel? Condition { get; set; }
    public string MissingValueBehavior { get; set; } = "Error";
    public string? DefaultValue { get; set; }
    public bool FirstOccurrence { get; set; }
    public List<XmlStringOperationViewModel> Operations { get; set; } = [];
}

public sealed class XmlStringOperationViewModel
{
    public string Kind { get; set; } = "NormalizeWhitespace";
    public string? Argument { get; set; }
}

public sealed record XmlMappingPreviewRequestViewModel(string MappingJson, string SampleXml);
public sealed record XmlMappingPreviewViewModel(string OutputXml, string Xslt);
public sealed record XmlSchemaAnalysisRequestViewModel(IReadOnlyList<string> Schemas);
public sealed record XmlMappingImportRequestViewModel(string MapXml, IReadOnlyList<string> SourceSchemas, IReadOnlyList<string> TargetSchemas);
public sealed record NativeMapConversionViewModel(string MappingJson, string Xslt, IReadOnlyList<string> Warnings, IReadOnlyList<string>? PendingFunctions = null);
public sealed record XmlSchemaTreeViewModel(IReadOnlyDictionary<string, string> Namespaces, IReadOnlyList<XmlSchemaRootViewModel> Roots);
public sealed record XmlSchemaRootViewModel(string Name, string Path, IReadOnlyList<XmlSchemaFieldViewModel> Fields);
public sealed record XmlSchemaFieldViewModel(string Path, string LocalPath, bool Scalar, bool Required, bool Repeating, bool HasDefaultOrFixed, string? UnsupportedReason, bool SelfRepeating = false, bool Nillable = false);
public sealed record XmlSchemaSelectionViewModel(XmlSchemaRootViewModel Root, XmlSchemaFieldViewModel Field, IReadOnlyDictionary<string, string> Namespaces);
public sealed record XmlSchemaFieldPairViewModel(XmlSchemaSelectionViewModel Source, XmlSchemaSelectionViewModel Target);
public sealed record XmlMappingSchemasViewModel(IReadOnlyList<string> SourceSchemas, IReadOnlyList<string> TargetSchemas);
public sealed record XmlMappingTreesViewModel(XmlSchemaTreeViewModel? Source, XmlSchemaTreeViewModel? Target);
