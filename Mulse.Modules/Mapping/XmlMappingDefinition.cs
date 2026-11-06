namespace Mulse.Modules.Mapping;

public sealed record XmlMappingDefinition
{
    public int Version { get; init; } = 1;
    public string? SourceRoot { get; init; }
    public string TargetRoot { get; init; } = "Output";
    public Dictionary<string, string> Namespaces { get; init; } = new(StringComparer.Ordinal);
    public IReadOnlyList<XmlFieldMappingDefinition> Fields { get; init; } = [];
    public IReadOnlyList<XmlRepeatMappingDefinition> Repeats { get; init; } = [];
    public IReadOnlyList<string> SourceSchemas { get; init; } = [];
    public IReadOnlyList<string> TargetSchemas { get; init; } = [];
    /// <summary>When false the schemas still describe the map for editing, but source messages are not XSD-validated at run time.</summary>
    public bool ValidateSource { get; init; } = true;
    /// <summary>When false the mapped output is not XSD-validated at run time.</summary>
    public bool ValidateTarget { get; init; } = true;
}

/// <summary>
/// Emits the target element once per selected source element. Fields below the target element read from the same
/// source element (or a nested repeat of it), so records stay correlated.
/// </summary>
public sealed record XmlRepeatMappingDefinition
{
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
}

public sealed record XmlFieldMappingDefinition
{
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string? Constant { get; init; }
    public MissingXmlValueBehavior MissingValueBehavior { get; init; } = MissingXmlValueBehavior.Error;
    public string? DefaultValue { get; init; }
    public IReadOnlyList<XmlStringOperation> Operations { get; init; } = [];
}

public sealed record XmlStringOperation
{
    public XmlStringOperationKind Kind { get; init; } = XmlStringOperationKind.NormalizeWhitespace;
    public string? Argument { get; init; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<XmlStringOperationKind>))]
public enum XmlStringOperationKind
{
    NormalizeWhitespace,
    UppercaseAscii,
    LowercaseAscii,
    Prefix,
    Suffix
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<MissingXmlValueBehavior>))]
public enum MissingXmlValueBehavior
{
    Error,
    Omit,
    Default
}
