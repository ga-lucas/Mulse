namespace Mulse.Modules;

/// <summary>Which of a join's two named inputs (or a constant) a field mapping reads from.</summary>
public enum WorkflowFieldSourceKind
{
    /// <summary>Read from the left row currently being mapped.</summary>
    Left,

    /// <summary>Read from the right row currently being mapped.</summary>
    Right,

    /// <summary>Use the mapping's literal value.</summary>
    Literal
}
