using Mulse.Modules;

namespace Service;

internal sealed class ModuleRegistryBuilder : IModuleRegistryBuilder
{
    private readonly List<(ModuleKind Kind, Type ImplementationType)> _registrations = [];

    public IReadOnlyList<(ModuleKind Kind, Type ImplementationType)> Registrations => _registrations;

    public void AddInput<TModule>() where TModule : class, IInputModule
    {
        _registrations.Add((ModuleKind.Input, typeof(TModule)));
    }

    public void AddOrchestrationAugment<TModule>() where TModule : class, IOrchestrationAugmentModule
    {
        _registrations.Add((ModuleKind.OrchestrationAugment, typeof(TModule)));
    }

    public void AddOutput<TModule>() where TModule : class, IOutputModule
    {
        _registrations.Add((ModuleKind.Output, typeof(TModule)));
    }
}
