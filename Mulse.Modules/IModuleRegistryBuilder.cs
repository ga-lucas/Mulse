namespace Mulse.Modules;

public interface IModuleRegistryBuilder
{
    void AddInput<TModule>() where TModule : class, IInputModule;

    void AddOrchestrationAugment<TModule>() where TModule : class, IOrchestrationAugmentModule;

    void AddOutput<TModule>() where TModule : class, IOutputModule;
}
