namespace Service.Modules;

public sealed class ManagedModulePackageDefinition
{
    public string Id { get; init; } = string.Empty;

    public string AssemblyPath { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;
}
