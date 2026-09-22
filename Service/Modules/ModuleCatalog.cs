using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Mulse.Modules;

namespace Service.Modules;

public sealed class ModuleCatalog : IModuleCatalog
{
    private readonly object _syncRoot = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly IRuntimeConfigurationStore _runtimeConfigurationStore;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModuleCatalog> _logger;
    private readonly Dictionary<string, RegisteredPackage> _packagesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegisteredModule> _modulesById = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _synchronizationGate = new(1, 1);

    public ModuleCatalog(
        IServiceProvider serviceProvider,
        IRuntimeConfigurationStore runtimeConfigurationStore,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<ModuleCatalog> logger)
    {
        _serviceProvider = serviceProvider;
        _runtimeConfigurationStore = runtimeConfigurationStore;
        _environment = environment;
        _timeProvider = timeProvider;
        _logger = logger;

        var builtInAssemblyPath = typeof(BuiltInModuleInstaller).Assembly.Location;
        var builtInPackage = CreatePackageFromInstaller(
            packageId: "builtin.core",
            sourceKind: RuntimePackageSourceKind.BuiltIn,
            sourceAssemblyPath: builtInAssemblyPath,
            installerFactory: static () => [new BuiltInModuleInstaller()],
            loadContext: null,
            sourceFileLastWriteTimeUtc: File.Exists(builtInAssemblyPath)
                ? File.GetLastWriteTimeUtc(builtInAssemblyPath)
                : DateTime.UtcNow);
        var compatibilityAssemblyPath = typeof(Mulse.CompatibilityPack.CompatibilityPackInstaller).Assembly.Location;
        var compatibilityPackage = CreatePackageFromInstaller(
            packageId: "builtin.compatibility",
            sourceKind: RuntimePackageSourceKind.BuiltIn,
            sourceAssemblyPath: compatibilityAssemblyPath,
            installerFactory: static () => [new Mulse.CompatibilityPack.CompatibilityPackInstaller()],
            loadContext: null,
            sourceFileLastWriteTimeUtc: File.Exists(compatibilityAssemblyPath)
                ? File.GetLastWriteTimeUtc(compatibilityAssemblyPath)
                : DateTime.UtcNow);
        var hl7AssemblyPath = typeof(Mulse.Hl7.Hl7ModuleInstaller).Assembly.Location;
        var hl7Package = CreatePackageFromInstaller(
            packageId: "builtin.hl7",
            sourceKind: RuntimePackageSourceKind.BuiltIn,
            sourceAssemblyPath: hl7AssemblyPath,
            installerFactory: static () => [new Mulse.Hl7.Hl7ModuleInstaller()],
            loadContext: null,
            sourceFileLastWriteTimeUtc: File.Exists(hl7AssemblyPath)
                ? File.GetLastWriteTimeUtc(hl7AssemblyPath)
                : DateTime.UtcNow);
        var sqlServerAssemblyPath = typeof(Mulse.Dbms.SqlServer.SqlServerModuleInstaller).Assembly.Location;
        var sqlServerPackage = CreatePackageFromInstaller(
            packageId: "builtin.dbms.sqlserver",
            sourceKind: RuntimePackageSourceKind.BuiltIn,
            sourceAssemblyPath: sqlServerAssemblyPath,
            installerFactory: static () => [new Mulse.Dbms.SqlServer.SqlServerModuleInstaller()],
            loadContext: null,
            sourceFileLastWriteTimeUtc: File.Exists(sqlServerAssemblyPath)
                ? File.GetLastWriteTimeUtc(sqlServerAssemblyPath)
                : DateTime.UtcNow);

        AddOrReplacePackage(builtInPackage, replaceExisting: true, previousPackage: out _);
        AddOrReplacePackage(compatibilityPackage, replaceExisting: true, previousPackage: out _);
        AddOrReplacePackage(hl7Package, replaceExisting: true, previousPackage: out _);
        AddOrReplacePackage(sqlServerPackage, replaceExisting: true, previousPackage: out _);
    }

    public IReadOnlyList<ModuleCatalogEntry> GetAll()
    {
        lock (_syncRoot)
        {
            return _modulesById.Values
                .Select(static module => module.Entry)
                .OrderBy(static entry => entry.Descriptor.Kind)
                .ThenBy(static entry => entry.Descriptor.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public IReadOnlyList<ModulePackageInfo> GetPackages()
    {
        lock (_syncRoot)
        {
            var loadedPackages = _packagesById.Values
                .Select(static package => package.ToInfo())
                .ToArray();
            var loadedIds = loadedPackages.Select(static package => package.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var unloadedManagedPackages = _runtimeConfigurationStore.GetState().ManagedPackages
                .Where(package => !loadedIds.Contains(package.Id))
                .Select(static package => new ModulePackageInfo(
                    package.Id,
                    package.AssemblyPath,
                    RuntimePackageSourceKind.Managed,
                    false,
                    DateTimeOffset.MinValue,
                    0));

            return loadedPackages
                .Concat(unloadedManagedPackages)
                .OrderBy(static package => package.SourceKind)
                .ThenBy(static package => package.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public ValueTask<ModuleLease<IFetchModule>> LeaseFetchAsync(string moduleId, CancellationToken cancellationToken)
    {
        return LeaseAsync<IFetchModule>(moduleId, ModuleKind.Fetch, cancellationToken);
    }

    public ValueTask<ModuleLease<IParseModule>> LeaseParseAsync(string moduleId, CancellationToken cancellationToken)
    {
        return LeaseAsync<IParseModule>(moduleId, ModuleKind.Parse, cancellationToken);
    }

    public ValueTask<ModuleLease<IOrchestrationAugmentModule>> LeaseOrchestrationAugmentAsync(string moduleId, CancellationToken cancellationToken)
    {
        return LeaseAsync<IOrchestrationAugmentModule>(moduleId, ModuleKind.OrchestrationAugment, cancellationToken);
    }

    public ValueTask<ModuleLease<IRenderModule>> LeaseRenderAsync(string moduleId, CancellationToken cancellationToken)
    {
        return LeaseAsync<IRenderModule>(moduleId, ModuleKind.Render, cancellationToken);
    }

    public ValueTask<ModuleLease<IDeliverModule>> LeaseDeliverAsync(string moduleId, CancellationToken cancellationToken)
    {
        return LeaseAsync<IDeliverModule>(moduleId, ModuleKind.Deliver, cancellationToken);
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken)
    {
        await _synchronizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var runtimeState = _runtimeConfigurationStore.GetState();

            foreach (var managedPackage in runtimeState.ManagedPackages)
            {
                if (!managedPackage.Enabled)
                {
                    await UnloadPackageIfExistsAsync(managedPackage.Id, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await LoadOrReloadPackageAsync(managedPackage.Id, managedPackage.AssemblyPath, RuntimePackageSourceKind.Managed, cancellationToken).ConfigureAwait(false);
            }

            var managedPackageIds = runtimeState.ManagedPackages.Select(static package => package.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var staleManagedPackageId in GetPackages()
                .Where(static package => package.SourceKind == RuntimePackageSourceKind.Managed)
                .Select(static package => package.Id)
                .Where(packageId => !managedPackageIds.Contains(packageId))
                .ToArray())
            {
                await UnloadPackageIfExistsAsync(staleManagedPackageId, cancellationToken).ConfigureAwait(false);
            }

            var autoDiscoveredPackages = DiscoverAutoPackages(runtimeState.PluginDirectories);
            foreach (var autoPackage in autoDiscoveredPackages)
            {
                if (managedPackageIds.Contains(autoPackage.Id))
                {
                    continue;
                }

                await LoadOrReloadPackageAsync(autoPackage.Id, autoPackage.AssemblyPath, RuntimePackageSourceKind.AutoDiscovered, cancellationToken).ConfigureAwait(false);
            }

            var discoveredIds = autoDiscoveredPackages.Select(static package => package.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var staleAutoPackageId in GetPackages()
                .Where(static package => package.SourceKind == RuntimePackageSourceKind.AutoDiscovered)
                .Select(static package => package.Id)
                .Where(packageId => !discoveredIds.Contains(packageId))
                .ToArray())
            {
                await UnloadPackageIfExistsAsync(staleAutoPackageId, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _synchronizationGate.Release();
        }
    }

    public async Task<ModulePackageInfo> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(package.Id))
        {
            throw new ArgumentException("Package id is required.", nameof(package));
        }

        if (string.IsNullOrWhiteSpace(package.AssemblyPath))
        {
            throw new ArgumentException("Assembly path is required.", nameof(package));
        }

        await _runtimeConfigurationStore.UpsertManagedPackageAsync(package, cancellationToken).ConfigureAwait(false);
        await SynchronizeAsync(cancellationToken).ConfigureAwait(false);
        return GetPackages().First(packageInfo => string.Equals(packageInfo.Id, package.Id, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<ModulePackageInfo> ReloadPackageAsync(string packageId, CancellationToken cancellationToken)
    {
        await _synchronizationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var package = GetRegisteredPackage(packageId);
            if (package.SourceKind == RuntimePackageSourceKind.BuiltIn)
            {
                throw new InvalidOperationException("Built-in packages cannot be reloaded.");
            }

            await LoadOrReloadPackageAsync(package.Id, package.SourceAssemblyPath, package.SourceKind, cancellationToken).ConfigureAwait(false);
            return GetPackages().First(packageInfo => string.Equals(packageInfo.Id, package.Id, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _synchronizationGate.Release();
        }
    }

    public async Task RemoveManagedPackageAsync(string packageId, CancellationToken cancellationToken)
    {
        await _runtimeConfigurationStore.DeleteManagedPackageAsync(packageId, cancellationToken).ConfigureAwait(false);
        await UnloadPackageIfExistsAsync(packageId, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<ModuleLease<TModule>> LeaseAsync<TModule>(string moduleId, ModuleKind kind, CancellationToken cancellationToken) where TModule : class, IModule
    {
        cancellationToken.ThrowIfCancellationRequested();

        RegisteredModule moduleState;
        lock (_syncRoot)
        {
            if (!_modulesById.TryGetValue(moduleId, out moduleState!))
            {
                throw new KeyNotFoundException($"No {kind} module with id '{moduleId}' is registered.");
            }

            if (moduleState.Entry.Descriptor.Kind != kind)
            {
                throw new InvalidOperationException($"Module '{moduleId}' is not a {kind} module.");
            }

            moduleState.Package.ActiveLeaseCount++;
        }

        try
        {
            var instance = (TModule)ActivatorUtilities.CreateInstance(_serviceProvider, moduleState.ImplementationType);
            return new ModuleLease<TModule>(instance, moduleState.Entry, async () =>
            {
                await DisposeModuleAsync(instance).ConfigureAwait(false);
                ReleaseLease(moduleState.Package);
            });
        }
        catch
        {
            ReleaseLease(moduleState.Package);
            throw;
        }
    }

    private RegisteredPackage GetRegisteredPackage(string packageId)
    {
        lock (_syncRoot)
        {
            if (_packagesById.TryGetValue(packageId, out var package))
            {
                return package;
            }
        }

        throw new KeyNotFoundException($"No module package with id '{packageId}' is registered.");
    }

    private IReadOnlyList<ManagedModulePackageDefinition> DiscoverAutoPackages(IEnumerable<string> pluginDirectories)
    {
        var discoveredPackages = new Dictionary<string, ManagedModulePackageDefinition>(StringComparer.OrdinalIgnoreCase);

        foreach (var configuredDirectory in pluginDirectories.Where(static directory => !string.IsNullOrWhiteSpace(directory)))
        {
            var fullDirectory = Path.GetFullPath(Path.IsPathRooted(configuredDirectory)
                ? configuredDirectory
                : Path.Combine(_environment.ContentRootPath, configuredDirectory));

            if (!Directory.Exists(fullDirectory))
            {
                continue;
            }

            foreach (var assemblyPath in Directory.EnumerateFiles(fullDirectory, "*.dll", SearchOption.AllDirectories)
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
            {
                var packageId = Path.GetFileNameWithoutExtension(assemblyPath);
                if (string.Equals(packageId, "Mulse.Modules", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                discoveredPackages.TryAdd(packageId, new ManagedModulePackageDefinition
                {
                    Id = packageId,
                    AssemblyPath = assemblyPath,
                    Enabled = true
                });
            }
        }

        return discoveredPackages.Values.ToArray();
    }

    private async Task LoadOrReloadPackageAsync(string packageId, string assemblyPath, RuntimePackageSourceKind sourceKind, CancellationToken cancellationToken)
    {
        var fullAssemblyPath = Path.GetFullPath(Path.IsPathRooted(assemblyPath)
            ? assemblyPath
            : Path.Combine(_environment.ContentRootPath, assemblyPath));

        if (!File.Exists(fullAssemblyPath))
        {
            throw new ArgumentException($"Plugin assembly '{fullAssemblyPath}' was not found.", nameof(assemblyPath));
        }

        var sourceLastWriteTimeUtc = File.GetLastWriteTimeUtc(fullAssemblyPath);
        var existingPackage = TryGetPackage(packageId);
        if (existingPackage is not null
            && string.Equals(existingPackage.SourceAssemblyPath, fullAssemblyPath, StringComparison.OrdinalIgnoreCase)
            && existingPackage.SourceKind == sourceKind
            && existingPackage.SourceFileLastWriteTimeUtc == sourceLastWriteTimeUtc)
        {
            return;
        }

        var package = CreatePackageFromAssembly(packageId, sourceKind, fullAssemblyPath, sourceLastWriteTimeUtc);
        AddOrReplacePackage(package, replaceExisting: true, previousPackage: out var previousPackage);
        if (previousPackage is not null)
        {
            await UnloadPackageAsync(previousPackage, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Loaded module package {PackageId} from {AssemblyPath}.", packageId, fullAssemblyPath);
    }

    private RegisteredPackage? TryGetPackage(string packageId)
    {
        lock (_syncRoot)
        {
            _packagesById.TryGetValue(packageId, out var package);
            return package;
        }
    }

    private void AddOrReplacePackage(RegisteredPackage package, bool replaceExisting, out RegisteredPackage? previousPackage)
    {
        lock (_syncRoot)
        {
            previousPackage = null;
            if (_packagesById.TryGetValue(package.Id, out var existingPackage))
            {
                if (!replaceExisting)
                {
                    throw new InvalidOperationException($"Package '{package.Id}' is already loaded.");
                }

                previousPackage = existingPackage;
            }

            foreach (var module in package.Modules.Values)
            {
                if (_modulesById.TryGetValue(module.Entry.Descriptor.Id, out var existingModule)
                    && !string.Equals(existingModule.Package.Id, package.Id, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Module id '{module.Entry.Descriptor.Id}' is already provided by package '{existingModule.Package.Id}'.");
                }
            }

            if (previousPackage is not null)
            {
                foreach (var moduleId in previousPackage.Modules.Keys)
                {
                    _modulesById.Remove(moduleId);
                }
            }

            _packagesById[package.Id] = package;
            foreach (var module in package.Modules)
            {
                _modulesById[module.Key] = module.Value;
            }
        }
    }

    private RegisteredPackage CreatePackageFromAssembly(string packageId, RuntimePackageSourceKind sourceKind, string sourceAssemblyPath, DateTime sourceFileLastWriteTimeUtc)
    {
        var shadowAssemblyPath = ShadowCopyAssembly(packageId, sourceAssemblyPath);
        var loadContext = new PluginLoadContext(shadowAssemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(shadowAssemblyPath);
        return CreatePackageFromInstaller(
            packageId,
            sourceKind,
            sourceAssemblyPath,
            () => CreateInstallersFromAssembly(assembly),
            loadContext,
            sourceFileLastWriteTimeUtc);
    }

    private RegisteredPackage CreatePackageFromInstaller(
        string packageId,
        RuntimePackageSourceKind sourceKind,
        string sourceAssemblyPath,
        Func<IReadOnlyList<IModuleInstaller>> installerFactory,
        PluginLoadContext? loadContext,
        DateTime sourceFileLastWriteTimeUtc)
    {
        var builder = new ModuleRegistryBuilder();
        foreach (var installer in installerFactory())
        {
            installer.Install(builder);
        }

        var package = new RegisteredPackage(
            packageId,
            sourceKind,
            sourceAssemblyPath,
            _timeProvider.GetUtcNow(),
            sourceFileLastWriteTimeUtc,
            loadContext);

        foreach (var registration in builder.Registrations)
        {
            var module = (IModule)ActivatorUtilities.CreateInstance(_serviceProvider, registration.ImplementationType);
            try
            {
                if (module.Descriptor.Kind != registration.Kind)
                {
                    throw new InvalidOperationException($"Module '{registration.ImplementationType.FullName}' declared descriptor kind '{module.Descriptor.Kind}' but was registered as '{registration.Kind}'.");
                }

                var entry = new ModuleCatalogEntry(
                    packageId,
                    sourceKind,
                    sourceAssemblyPath,
                    module.Descriptor);

                package.Modules.Add(entry.Descriptor.Id, new RegisteredModule(entry, registration.ImplementationType, package));
            }
            finally
            {
                DisposeModuleAsync(module).GetAwaiter().GetResult();
            }
        }

        return package;
    }

    private static IReadOnlyList<IModuleInstaller> CreateInstallersFromAssembly(Assembly assembly)
    {
        return assembly.GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IModuleInstaller).IsAssignableFrom(type))
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .Select(type => (IModuleInstaller)(Activator.CreateInstance(type)
                ?? throw new InvalidOperationException($"Unable to instantiate module installer '{type.FullName}'.")))
            .ToArray();
    }

    private string ShadowCopyAssembly(string packageId, string sourceAssemblyPath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourceAssemblyPath)
            ?? throw new InvalidOperationException($"Assembly path '{sourceAssemblyPath}' does not have a parent directory.");
        var shadowDirectory = Path.Combine(_environment.ContentRootPath, "Runtime", "PluginCache", packageId, Guid.CreateVersion7().ToString());
        Directory.CreateDirectory(shadowDirectory);

        foreach (var filePath in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.TopDirectoryOnly))
        {
            var targetPath = Path.Combine(shadowDirectory, Path.GetFileName(filePath));
            File.Copy(filePath, targetPath, overwrite: true);
        }

        return Path.Combine(shadowDirectory, Path.GetFileName(sourceAssemblyPath));
    }

    private async Task UnloadPackageIfExistsAsync(string packageId, CancellationToken cancellationToken)
    {
        RegisteredPackage? package;
        lock (_syncRoot)
        {
            if (!_packagesById.TryGetValue(packageId, out package))
            {
                return;
            }

            if (package.SourceKind == RuntimePackageSourceKind.BuiltIn)
            {
                throw new InvalidOperationException("Built-in packages cannot be unloaded.");
            }

            _packagesById.Remove(packageId);
            foreach (var moduleId in package.Modules.Keys)
            {
                _modulesById.Remove(moduleId);
            }
        }

        await UnloadPackageAsync(package, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Unloaded module package {PackageId}.", packageId);
    }

    private async Task UnloadPackageAsync(RegisteredPackage package, CancellationToken cancellationToken)
    {
        Task waitForLeasesTask;
        lock (_syncRoot)
        {
            package.IsUnloading = true;
            waitForLeasesTask = package.ActiveLeaseCount == 0 ? Task.CompletedTask : package.WhenIdle.Task;
        }

        await waitForLeasesTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        package.LoadContext?.Unload();
    }

    private void ReleaseLease(RegisteredPackage package)
    {
        lock (_syncRoot)
        {
            package.ActiveLeaseCount--;
            if (package.ActiveLeaseCount == 0 && package.IsUnloading)
            {
                package.WhenIdle.TrySetResult();
            }
        }
    }

    private static async ValueTask DisposeModuleAsync(object module)
    {
        switch (module)
        {
            case IAsyncDisposable asyncDisposable:
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }

    private sealed class RegisteredPackage
    {
        public RegisteredPackage(
            string id,
            RuntimePackageSourceKind sourceKind,
            string sourceAssemblyPath,
            DateTimeOffset lastLoadedAt,
            DateTime sourceFileLastWriteTimeUtc,
            PluginLoadContext? loadContext)
        {
            Id = id;
            SourceKind = sourceKind;
            SourceAssemblyPath = sourceAssemblyPath;
            LastLoadedAt = lastLoadedAt;
            SourceFileLastWriteTimeUtc = sourceFileLastWriteTimeUtc;
            LoadContext = loadContext;
        }

        public string Id { get; }

        public RuntimePackageSourceKind SourceKind { get; }

        public string SourceAssemblyPath { get; }

        public DateTimeOffset LastLoadedAt { get; }

        public DateTime SourceFileLastWriteTimeUtc { get; }

        public PluginLoadContext? LoadContext { get; }

        public Dictionary<string, RegisteredModule> Modules { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int ActiveLeaseCount { get; set; }

        public bool IsUnloading { get; set; }

        public TaskCompletionSource WhenIdle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ModulePackageInfo ToInfo() => new(Id, SourceAssemblyPath, SourceKind, true, LastLoadedAt, Modules.Count);
    }

    private sealed record RegisteredModule(ModuleCatalogEntry Entry, Type ImplementationType, RegisteredPackage Package);
}
