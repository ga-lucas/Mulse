namespace Mulse.Modules;

public sealed class BuiltInModuleInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddInput<FileSystemSourceModule>();
        builder.AddOrchestrationAugment<JsonEnvelopeAugmentTransformModule>();
        builder.AddOutput<LoggingEventModule>();
        builder.AddOutput<FileSystemStorageModule>();
    }
}
