using Mulse.Modules;

namespace Service.Modules;

internal sealed class ModuleRegistryBuilder : IModuleRegistryBuilder
{
    private readonly List<(ModuleKind Kind, Type ImplementationType)> _registrations = [];

    public IReadOnlyList<(ModuleKind Kind, Type ImplementationType)> Registrations => _registrations;

    public void AddFetch<TModule>() where TModule : class, IFetchModule
    {
        _registrations.Add((ModuleKind.Fetch, typeof(TModule)));
    }

    public void AddParse<TModule>() where TModule : class, IParseModule
    {
        _registrations.Add((ModuleKind.Parse, typeof(TModule)));
    }

    public void AddOrchestrationAugment<TModule>() where TModule : class, IOrchestrationAugmentModule
    {
        _registrations.Add((ModuleKind.OrchestrationAugment, typeof(TModule)));
    }

    public void AddRender<TModule>() where TModule : class, IRenderModule
    {
        _registrations.Add((ModuleKind.Render, typeof(TModule)));
    }

    public void AddDeliver<TModule>() where TModule : class, IDeliverModule
    {
        _registrations.Add((ModuleKind.Deliver, typeof(TModule)));
    }
}
