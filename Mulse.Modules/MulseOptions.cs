namespace Mulse.Modules;

public sealed class MulseOptions
{
    public string RuntimeStatePath { get; init; } = "Data\\mulse-runtime.json";

    public List<string> PluginDirectories { get; init; } = [];

    public List<PipelineDefinition> Pipelines { get; init; } = [];
}
