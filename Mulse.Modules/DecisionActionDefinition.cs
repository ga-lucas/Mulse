namespace Mulse.Modules;

public sealed class DecisionActionDefinition
{
    public DecisionActionKind Kind { get; init; } = DecisionActionKind.SetMetadata;

    public string Target { get; init; } = string.Empty;

    public DecisionValueSourceKind ValueSource { get; init; } = DecisionValueSourceKind.Literal;

    public string? SourcePath { get; init; }

    public string? Value { get; init; }
}
