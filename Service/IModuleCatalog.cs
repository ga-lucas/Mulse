using Mulse.Modules;

namespace Service;

public interface IModuleCatalog
{
    IReadOnlyList<ModuleCatalogEntry> GetAll();

    IReadOnlyList<ModulePackageInfo> GetPackages();

    ValueTask<ModuleLease<IInputModule>> LeaseInputAsync(string moduleId, CancellationToken cancellationToken);

    ValueTask<ModuleLease<IOrchestrationAugmentModule>> LeaseOrchestrationAugmentAsync(string moduleId, CancellationToken cancellationToken);

    ValueTask<ModuleLease<IOutputModule>> LeaseOutputAsync(string moduleId, CancellationToken cancellationToken);

    Task SynchronizeAsync(CancellationToken cancellationToken);

    Task<ModulePackageInfo> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken);

    Task<ModulePackageInfo> ReloadPackageAsync(string packageId, CancellationToken cancellationToken);

    Task RemoveManagedPackageAsync(string packageId, CancellationToken cancellationToken);
}
