namespace Mulse.Modules.Mapping;

public sealed record XmlMappingDefinition
{
    public int Version { get; init; } = 1;
    public string? SourceRoot { get; init; }
    public string TargetRoot { get; init; } = "Output";
    public Dictionary<string, string> Namespaces { get; init; } = new(StringComparer.Ordinal);
    public IReadOnlyList<XmlFieldMappingDefinition> Fields { get; init; } = [];
    public IReadOnlyList<string> SourceSchemas { get; init; } = [];
    public IReadOnlyList<string> TargetSchemas { get; init; } = [];
}

public sealed record XmlFieldMappingDefinition
{
    public string SourcePath { get; init; } = string.Empty;
    public string TargetPath { get; init; } = string.Empty;
    public string? Constant { get; init; }
    public MissingXmlValueBehavior MissingValueBehavior { get; init; } = MissingXmlValueBehavior.Error;
    public string? DefaultValue { get; init; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<MissingXmlValueBehavior>))]
public enum MissingXmlValueBehavior
{
    Error,
    Omit,
    Default
}
