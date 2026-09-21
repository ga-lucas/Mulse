namespace Mulse.Modules;

public sealed record WorkflowFieldMappingDefinition
{
    public WorkflowFieldSourceKind SourceKind { get; init; } = WorkflowFieldSourceKind.Source;

    public string SourcePath { get; init; } = string.Empty;

    public string TargetField { get; init; } = string.Empty;

    public WorkflowFieldCondition Condition { get; init; } = WorkflowFieldCondition.Always;

    public string? LiteralValue { get; init; }
}
