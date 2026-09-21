namespace Mulse.Modules;

public interface IModuleRegistryBuilder
{
    void AddFetch<TModule>() where TModule : class, IFetchModule;

    void AddParse<TModule>() where TModule : class, IParseModule;

    void AddOrchestrationAugment<TModule>() where TModule : class, IOrchestrationAugmentModule;

    void AddRender<TModule>() where TModule : class, IRenderModule;

    void AddDeliver<TModule>() where TModule : class, IDeliverModule;
}
