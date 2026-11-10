namespace Mulse.CompatibilityPack.Mapping;

public sealed record XmlMappingDefinition
{
    public int Version { get; init; } = 1;
    public string? SourceRoot { get; init; }
    public string TargetRoot { get; init; } = "Output";
    public Dictionary<string, string> Namespaces { get; init; } = new(StringComparer.Ordinal);
    public IReadOnlyList<XmlFieldMappingDefinition> Fields { get; init; } = [];
    public IReadOnlyList<XmlRepeatMappingDefinition> Repeats { get; init; } = [];
    /// <summary>Version 2: records that are written only when a condition holds, like a BizTalk logical functoid linked to a record.</summary>
    public IReadOnlyList<XmlRecordConditionDefinition> RecordConditions { get; init; } = [];
    /// <summary>Reusable functions (version 2). Imported script stubs live here until implemented.</summary>
    public IReadOnlyList<XmlCustomFunctionDefinition> CustomFunctions { get; init; } = [];
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

/// <summary>
/// Writes the target record (and everything below it) only when the condition evaluates to the exact text 'true'. For a repeating record the
/// condition is evaluated once per source record, inside its loop, so it can read that record's fields. Compiled BizTalk output wraps the record
/// in an xsl:if on the logical functoid's result the same way.
/// </summary>
public sealed record XmlRecordConditionDefinition
{
    public string TargetPath { get; init; } = string.Empty;
    public XmlValueExpression? Condition { get; init; }
    /// <summary>
    /// Optional absolute path of a repeating source record. The condition is then evaluated for each such record (its fields are read from that record) and the target record is written once when it holds for
    /// at least one of them, like a BizTalk logical functoid fed by a repeating record. When it holds for more than one the run fails, because BizTalk would write the record again.
    /// </summary>
    public string? AnySourcePath { get; init; }
}

public sealed record XmlFieldMappingDefinition
{
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string? Constant { get; init; }
    /// <summary>A computed value (source paths, constants and catalog functions). Mutually exclusive with SourcePath/Constant; the target is always emitted, like a BizTalk functoid output.</summary>
    public XmlValueExpression? Expression { get; init; }
    /// <summary>Only with an Expression (version 2): the target is emitted only when this expression evaluates to the exact text 'true', like a BizTalk Value Mapping functoid. Otherwise nothing is written for the target.</summary>
    public XmlValueExpression? Condition { get; init; }
    public MissingXmlValueBehavior MissingValueBehavior { get; init; } = MissingXmlValueBehavior.Error;
    public string? DefaultValue { get; init; }
    /// <summary>Version 2, source-path fields only: when the source path matches several nodes (a field below a repeating record), use the first one in document order instead of failing, as BizTalk does for a direct link with no record link around its target.</summary>
    public bool FirstOccurrence { get; init; }
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
    /// <summary>Kind Custom: the called custom function. Kind Parameter: the custom function parameter being read.</summary>
    public string? Name { get; init; }
    public IReadOnlyList<XmlValueExpression> Arguments { get; init; } = [];
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<XmlValueExpressionKind>))]
public enum XmlValueExpressionKind
{
    Source,
    Constant,
    Function,
    /// <summary>A call of a map-level custom function (for example an imported script stub).</summary>
    Custom,
    /// <summary>A custom function parameter; only valid inside a custom function body.</summary>
    Parameter
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
    LogicalExistence,
    // Mulse-native functions (not BizTalk functoids): the building blocks for replacing imported script stubs.
    NewGuid,
    DateNow,
    DateReformat,
    DateValid,
    Conditional,
    LogicalOr,
    LogicalNot,
    StringLowerCase,
    StringTrim,
    StringReplace,
    StringSubstring,
    StringContains,
    // BizTalk functoids ported from their documented behavior (only MathAbs was compared with a compiled helper body).
    MathAbs,
    MathAdd,
    MathSubtract,
    MathMultiply,
    LogicalGte,
    LogicalLt,
    LogicalLte,
    StringTrimLeft,
    StringTrimRight,
    // BizTalk Iteration functoid (474): BizTalk compiles it to position(); verified against a compiled map.
    RecordPosition,
    // BizTalk Record Count functoid (322): ported from its documented behavior (count of the nodes at a source path); not compared with a compiled map.
    RecordCount,
    // BizTalk cumulative functoids (324-328): ported from their documented behavior (fold over every selected node); not compared with a compiled map.
    CumulativeSum,
    CumulativeAverage,
    CumulativeMin,
    CumulativeMax,
    CumulativeConcat,
    // BizTalk Index functoid (323): ported from its documented behavior (the i-th occurrence of each repeating ancestor); not compared with a compiled map.
    IndexValue
}

/// <summary>
/// A map-level function with named parameters and an expression body built only from parameters, constants and catalog
/// functions. A function without a body is a stub (for example an imported BizTalk script): a map that calls it fails closed
/// until the body is supplied. The original code is evidence for the author, never executed.
/// </summary>
public sealed record XmlCustomFunctionDefinition
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public IReadOnlyList<XmlCustomFunctionParameter> Parameters { get; init; } = [];
    /// <summary>The implementation; null while the function is a stub.</summary>
    public XmlValueExpression? Body { get; init; }
    public string? OriginalSource { get; init; }
    public string? OriginalSourceLanguage { get; init; }
    /// <summary>Where the original was used: one entry per functoid, with its inputs and target.</summary>
    public IReadOnlyList<string> Usages { get; init; } = [];
}

public sealed record XmlCustomFunctionParameter
{
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
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
