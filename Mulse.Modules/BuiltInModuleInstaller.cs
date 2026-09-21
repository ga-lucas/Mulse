namespace Mulse.Modules;

public sealed class BuiltInModuleInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddFetch<FileSystemFetchModule>();
        builder.AddFetch<SftpFetchModule>();
        builder.AddParse<JsonParseModule>();
        builder.AddOrchestrationAugment<JsonEnvelopeAugmentTransformModule>();
        builder.AddOrchestrationAugment<DecisionAugmentModule>();
        builder.AddOrchestrationAugment<SqlServerLookupAugmentModule>();
        builder.AddOrchestrationAugment<ConditionalJoinMapAugmentModule>();
        builder.AddRender<JsonRenderModule>();
        builder.AddRender<XmlRenderModule>();
        builder.AddDeliver<LoggingDeliverModule>();
        builder.AddDeliver<FileSystemDeliverModule>();
        builder.AddDeliver<HttpDeliverModule>();
    }
}
