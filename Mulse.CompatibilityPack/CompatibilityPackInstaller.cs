using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class CompatibilityPackInstaller : IModuleInstaller
{
    public void Install(IModuleRegistryBuilder builder)
    {
        builder.AddFetch<DocumentRepositoryFetchModule>();
        builder.AddFetch<FileSystemWatcherFetchModule>();
        builder.AddFetch<HttpInboundFetchModule>();
        builder.AddParse<XmlXsdParseModule>();
        builder.AddParse<FlatFileParseModule>();
        builder.AddParse<XmlEnvelopeDebatchParseModule>();
        builder.AddOrchestrationAugment<MetadataPromotionAugmentModule>();
        builder.AddOrchestrationAugment<RouteSelectionAugmentModule>();
        builder.AddRender<FlatFileRenderModule>();
        builder.AddRender<XsltRenderModule>();
        builder.AddRender<SoapEnvelopeRenderModule>();
        builder.AddDeliver<DocumentRepositoryStoreModule>();
        builder.AddDeliver<SmtpEmailDeliverModule>();
    }
}
