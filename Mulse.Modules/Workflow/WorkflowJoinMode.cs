namespace Mulse.Modules.Workflow;

/// <summary>
/// Relational join semantics used by <see cref="MultiSourceJoinMapAugmentModule"/>.
/// </summary>
public enum WorkflowJoinMode
{
    /// <summary>Only matched left/right row pairs are emitted.</summary>
    Inner,

    /// <summary>Every left row is emitted; unmatched left rows emit once with the right-side mappings omitted.</summary>
    Left,

    /// <summary>Every right row is emitted; unmatched right rows emit once with the left-side mappings omitted.</summary>
    Right,

    /// <summary>Every left row (matched or not) plus every right row that matched nothing at all.</summary>
    Full
}
