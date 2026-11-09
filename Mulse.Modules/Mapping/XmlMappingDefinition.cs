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
    /// <summary>A computed value (source paths, constants and catalog functions). Mutually exclusive with SourcePath/Constant; the target is always emitted, like a BizTalk functoid output.</summary>
    public XmlValueExpression? Expression { get; init; }
    public MissingXmlValueBehavior MissingValueBehavior { get; init; } = MissingXmlValueBehavior.Error;
    public string? DefaultValue { get; init; }
    public IReadOnlyList<XmlStringOperation> Operations { get; init; } = [];
}

/// <summary>One node of a value expression: a source path, a literal, or a call of a fixed catalog function.</summary>
public sealed record XmlValueExpression
{
    public XmlValueExpressionKind Kind { get; init; } = XmlValueExpressionKind.Source;
    /// <summary>Absolute named source path (Kind Source). A missing node reads as empty text.</summary>
    public string? SourcePath { get; init; }
    public string? Constant { get; init; }
    public XmlMappingFunction? Function { get; init; }
    public IReadOnlyList<XmlValueExpression> Arguments { get; init; } = [];
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<XmlValueExpressionKind>))]
public enum XmlValueExpressionKind
{
    Source,
    Constant,
    Function
}

/// <summary>
/// Pure functions with the exact semantics of the matching BizTalk functoid (the compiled map's helper body). Each entry
/// names the functoid it was verified against. Logical functions yield the text true/false.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<XmlMappingFunction>))]
public enum XmlMappingFunction
{
    StringLeft,
    StringRight,
    StringSize,
    StringConcat,
    StringUpperCase,
    MathInt,
    MathDivide,
    DateCurrentDate,
    DateCurrentDateTime,
    LogicalGt,
    LogicalEq,
    LogicalNe,
    LogicalAnd,
    LogicalExistence
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
