namespace Mulse.Modules;

/// <summary>Controls whether a field mapping is applied for a given emitted join record.</summary>
public enum WorkflowFieldCondition
{
    /// <summary>Always apply the mapping.</summary>
    Always,

    /// <summary>Apply only when the row being emitted has a counterpart on the other side of the join.</summary>
    WhenMatched,

    /// <summary>Apply only when the row being emitted had no counterpart on the other side of the join.</summary>
    WhenUnmatched
}
