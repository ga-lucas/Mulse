namespace Mulse.Modules;

public sealed class MulseOptions
{
    public string RuntimeStatePath { get; init; } = "Data\\mulse-runtime.json";

    public List<string> PluginDirectories { get; init; } = [];

    public List<PipelineDefinition> Pipelines { get; init; } = [];

    /// <summary>Optional at-rest encryption for the runtime state file. Disabled by default for backward compatibility.</summary>
    public RuntimeStateEncryptionOptions Encryption { get; init; } = new();
}
