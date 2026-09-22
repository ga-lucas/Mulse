using Mulse.Modules;

namespace Mulse.SamplePlugin;

public sealed class SamplePluginInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddOrchestrationAugment<SamplePluginTransformModule>();
        builder.AddOrchestrationAugment<SamplePluginTypedAugmentModule>();
    }
}
