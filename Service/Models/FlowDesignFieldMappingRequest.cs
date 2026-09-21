using System.ComponentModel.DataAnnotations;
using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents a visual mapping row submitted from the flow designer.</summary>
public sealed record FlowDesignFieldMappingRequest
{
    /// <summary>The mapping source kind.</summary>
    [Required]
    public required WorkflowFieldSourceKind SourceKind { get; init; }

    /// <summary>The source field path or lookup column name.</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>The target field path in the mapped output document.</summary>
    [Required]
    public required string TargetField { get; init; }

    /// <summary>The condition that decides whether the mapping applies.</summary>
    [Required]
    public WorkflowFieldCondition Condition { get; init; } = WorkflowFieldCondition.Always;

    /// <summary>An optional literal value when the mapping source kind is Literal.</summary>
    public string? LiteralValue { get; init; }
}
