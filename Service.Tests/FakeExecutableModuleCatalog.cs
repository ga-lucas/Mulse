namespace Service.Tests;

/// <summary>
/// <see cref="IModuleCatalog"/> double that actually leases working fake modules, unlike the read-only
/// <c>FakeModuleCatalog</c> used by <c>FlowDefinitionServiceTests</c> (which only supports <see cref="GetAll"/>
/// and throws on every lease). Used by <see cref="FlowRuntime"/> tests that need to execute a flow end-to-end.
/// </summary>
internal sealed class FakeExecutableModuleCatalog : IModuleCatalog
{
    private readonly Dictionary<string, IFetchModule> _fetchModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IParseModule> _parseModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IOrchestrationAugmentModule> _augmentModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IRenderModule> _renderModules = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IDeliverModule> _deliverModules = new(StringComparer.OrdinalIgnoreCase);

    public FakeExecutableModuleCatalog AddFetch(IFetchModule module)
    {
        _fetchModules[module.Descriptor.Id] = module;
        return this;
    }

    public FakeExecutableModuleCatalog AddParse(IParseModule module)
    {
        _parseModules[module.Descriptor.Id] = module;
        return this;
    }

    public FakeExecutableModuleCatalog AddAugment(IOrchestrationAugmentModule module)
    {
        _augmentModules[module.Descriptor.Id] = module;
        return this;
    }

    public FakeExecutableModuleCatalog AddRender(IRenderModule module)
    {
        _renderModules[module.Descriptor.Id] = module;
        return this;
    }

    public FakeExecutableModuleCatalog AddDeliver(IDeliverModule module)
    {
        _deliverModules[module.Descriptor.Id] = module;
        return this;
    }

    public IReadOnlyList<ModuleCatalogEntry> GetAll() => throw new NotSupportedException();

    public IReadOnlyList<ModulePackageInfo> GetPackages() => throw new NotSupportedException();

    public ValueTask<ModuleLease<IFetchModule>> LeaseFetchAsync(string moduleId, CancellationToken cancellationToken)
        => Lease(_fetchModules, moduleId);

    public ValueTask<ModuleLease<IParseModule>> LeaseParseAsync(string moduleId, CancellationToken cancellationToken)
        => Lease(_parseModules, moduleId);

    public ValueTask<ModuleLease<IOrchestrationAugmentModule>> LeaseOrchestrationAugmentAsync(string moduleId, CancellationToken cancellationToken)
        => Lease(_augmentModules, moduleId);

    public ValueTask<ModuleLease<IRenderModule>> LeaseRenderAsync(string moduleId, CancellationToken cancellationToken)
        => Lease(_renderModules, moduleId);

    public ValueTask<ModuleLease<IDeliverModule>> LeaseDeliverAsync(string moduleId, CancellationToken cancellationToken)
        => Lease(_deliverModules, moduleId);

    public Task SynchronizeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<ModulePackageInfo> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ModulePackageInfo> ReloadPackageAsync(string packageId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task RemoveManagedPackageAsync(string packageId, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    private static ValueTask<ModuleLease<TModule>> Lease<TModule>(Dictionary<string, TModule> registry, string moduleId)
        where TModule : class, IModule
    {
        if (!registry.TryGetValue(moduleId, out var module))
        {
            throw new InvalidOperationException($"No fake module registered for id '{moduleId}'.");
        }

        var entry = new ModuleCatalogEntry("test-package", RuntimePackageSourceKind.BuiltIn, "test.dll", module.Descriptor);
        return ValueTask.FromResult(new ModuleLease<TModule>(module, entry, static () => ValueTask.CompletedTask));
    }
}
