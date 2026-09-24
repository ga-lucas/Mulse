namespace Mulse.Modules.Workflow;

public sealed record WorkflowFieldMappingDefinition
{
    public WorkflowFieldSourceKind SourceKind { get; init; } = WorkflowFieldSourceKind.Left;

    public string SourcePath { get; init; } = string.Empty;

    public string TargetField { get; init; } = string.Empty;

    public WorkflowFieldCondition Condition { get; init; } = WorkflowFieldCondition.Always;

    public string? LiteralValue { get; init; }

    /// <summary>The transform module id to invoke, when SourceKind is Transform.</summary>
    public string? TransformModule { get; init; }

    /// <summary>The transform's ordered inputs, when SourceKind is Transform.</summary>
    public IReadOnlyList<WorkflowTransformInputDefinition> TransformInputs { get; init; } = [];

    /// <summary>Settings passed to the transform module (e.g. a separator or format string), when SourceKind is Transform.</summary>
    public Dictionary<string, string> TransformSettings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
