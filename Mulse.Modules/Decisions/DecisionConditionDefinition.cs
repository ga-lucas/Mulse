namespace Mulse.Modules.Decisions;

public sealed class DecisionConditionDefinition
{
    public DecisionValueSourceKind Source { get; init; } = DecisionValueSourceKind.Metadata;

    public string Path { get; init; } = string.Empty;

    public DecisionComparisonOperator Operator { get; init; } = DecisionComparisonOperator.Exists;

    public string? Value { get; init; }
}
