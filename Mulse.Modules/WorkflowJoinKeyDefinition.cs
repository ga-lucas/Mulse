namespace Mulse.Modules;

/// <summary>
/// One equality predicate in a composite join key. Both paths use the
/// <see cref="JsonPayloadNavigator"/> dot-path convention (with optional trailing <c>[]</c> array expansion), so
/// a key may live inside a nested array on either side. A left row and a right row match when, for EVERY
/// configured key, at least one value selected by <see cref="LeftPath"/> equals at least one value selected by
/// <see cref="RightPath"/> (compared as normalized scalar text).
/// </summary>
public sealed record WorkflowJoinKeyDefinition
{
    public string LeftPath { get; init; } = string.Empty;

    public string RightPath { get; init; } = string.Empty;
}
