using Mulse.Modules;

namespace Service;

public sealed class RuntimeMulseState
{
    public List<string> PluginDirectories { get; set; } = [];

    public List<ManagedModulePackageDefinition> ManagedPackages { get; set; } = [];

    public List<PipelineDefinition> Pipelines { get; set; } = [];
}
