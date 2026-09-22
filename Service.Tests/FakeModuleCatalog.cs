using Mulse.Modules;
using Service;

namespace Service.Tests;

/// <summary>Minimal in-memory <see cref="IModuleCatalog"/> double for unit tests that only need <see cref="GetAll"/>.</summary>
internal sealed class FakeModuleCatalog(IReadOnlyList<ModuleCatalogEntry> entries) : IModuleCatalog
{
    public IReadOnlyList<ModuleCatalogEntry> GetAll() => entries;

    public IReadOnlyList<ModulePackageInfo> GetPackages() => throw new NotSupportedException();

    public ValueTask<ModuleLease<IFetchModule>> LeaseFetchAsync(string moduleId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask<ModuleLease<IParseModule>> LeaseParseAsync(string moduleId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask<ModuleLease<IOrchestrationAugmentModule>> LeaseOrchestrationAugmentAsync(string moduleId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask<ModuleLease<IRenderModule>> LeaseRenderAsync(string moduleId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public ValueTask<ModuleLease<IDeliverModule>> LeaseDeliverAsync(string moduleId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task SynchronizeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ModulePackageInfo> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ModulePackageInfo> ReloadPackageAsync(string packageId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task RemoveManagedPackageAsync(string packageId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public static FakeModuleCatalog WithModules(params (string Id, ModuleKind Kind)[] modules)
    {
        var entries = modules
            .Select(m => new ModuleCatalogEntry(
                "test-package",
                RuntimePackageSourceKind.BuiltIn,
                "test.dll",
                new ModuleDescriptor(m.Id, m.Id, m.Kind, $"Test module {m.Id}")))
            .ToArray();
        return new FakeModuleCatalog(entries);
    }
}
