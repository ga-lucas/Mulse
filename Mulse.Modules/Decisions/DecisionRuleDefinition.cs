namespace Mulse.Modules.Decisions;

public sealed class DecisionRuleDefinition
{
    public string Name { get; init; } = string.Empty;

    public List<DecisionConditionDefinition> Conditions { get; init; } = [];

    public List<DecisionActionDefinition> Actions { get; init; } = [];
}
