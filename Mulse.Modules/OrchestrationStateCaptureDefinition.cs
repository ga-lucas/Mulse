namespace Mulse.Modules;

public sealed class OrchestrationStateCaptureDefinition
{
    public string Key { get; init; } = string.Empty;

    public DecisionValueSourceKind Source { get; init; } = DecisionValueSourceKind.Metadata;

    public string? Path { get; init; }

    public string? Value { get; init; }
}
