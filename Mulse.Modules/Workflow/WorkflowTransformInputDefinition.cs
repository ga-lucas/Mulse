namespace Mulse.Modules.Workflow;

/// <summary>
/// One resolved input feeding a transform-kind field mapping. Only Left/Right/Literal are supported here
/// (no nested transforms) to keep the model simple and avoid unbounded recursion in v1.
/// </summary>
public sealed record WorkflowTransformInputDefinition
{
    public WorkflowFieldSourceKind SourceKind { get; init; } = WorkflowFieldSourceKind.Left;

    public string SourcePath { get; init; } = string.Empty;

    public string? LiteralValue { get; init; }
}
