using Mulse.Modules;

namespace Service;

public interface IModuleCatalog
{
    IReadOnlyList<ModuleCatalogEntry> GetAll();

    IReadOnlyList<ModulePackageInfo> GetPackages();

    ValueTask<ModuleLease<IFetchModule>> LeaseFetchAsync(string moduleId, CancellationToken cancellationToken);

    ValueTask<ModuleLease<IParseModule>> LeaseParseAsync(string moduleId, CancellationToken cancellationToken);

    ValueTask<ModuleLease<IOrchestrationAugmentModule>> LeaseOrchestrationAugmentAsync(string moduleId, CancellationToken cancellationToken);

    ValueTask<ModuleLease<IRenderModule>> LeaseRenderAsync(string moduleId, CancellationToken cancellationToken);

    ValueTask<ModuleLease<IDeliverModule>> LeaseDeliverAsync(string moduleId, CancellationToken cancellationToken);

    Task SynchronizeAsync(CancellationToken cancellationToken);

    Task<ModulePackageInfo> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken);

    Task<ModulePackageInfo> ReloadPackageAsync(string packageId, CancellationToken cancellationToken);

    Task RemoveManagedPackageAsync(string packageId, CancellationToken cancellationToken);
}
