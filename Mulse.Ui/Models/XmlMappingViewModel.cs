namespace Mulse.Ui.Models;

public sealed class XmlMappingViewModel
{
    public int Version { get; set; } = 1;
    public string? SourceRoot { get; set; }
    public string TargetRoot { get; set; } = "Output";
    public Dictionary<string, string> Namespaces { get; set; } = new(StringComparer.Ordinal);
    public List<XmlFieldMappingViewModel> Fields { get; set; } = [];
    public List<string> SourceSchemas { get; set; } = [];
    public List<string> TargetSchemas { get; set; } = [];
}

public sealed class XmlFieldMappingViewModel
{
    public string SourcePath { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string? Constant { get; set; }
    public string MissingValueBehavior { get; set; } = "Error";
    public string? DefaultValue { get; set; }
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
public sealed record NativeMapConversionViewModel(string MappingJson, string Xslt, IReadOnlyList<string> Warnings);
public sealed record XmlSchemaTreeViewModel(IReadOnlyDictionary<string, string> Namespaces, IReadOnlyList<XmlSchemaRootViewModel> Roots);
public sealed record XmlSchemaRootViewModel(string Name, string Path, IReadOnlyList<XmlSchemaFieldViewModel> Fields);
public sealed record XmlSchemaFieldViewModel(string Path, string LocalPath, bool Scalar, bool Required, bool Repeating, bool HasDefaultOrFixed, string? UnsupportedReason);
public sealed record XmlSchemaSelectionViewModel(XmlSchemaRootViewModel Root, XmlSchemaFieldViewModel Field, IReadOnlyDictionary<string, string> Namespaces);
public sealed record XmlSchemaFieldPairViewModel(XmlSchemaSelectionViewModel Source, XmlSchemaSelectionViewModel Target);
public sealed record XmlMappingSchemasViewModel(IReadOnlyList<string> SourceSchemas, IReadOnlyList<string> TargetSchemas);
public sealed record XmlMappingTreesViewModel(XmlSchemaTreeViewModel? Source, XmlSchemaTreeViewModel? Target);
