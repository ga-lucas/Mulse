namespace Mulse.Modules;

public sealed class ModuleStepDefinition
{
    public string Module { get; init; } = string.Empty;

    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}
