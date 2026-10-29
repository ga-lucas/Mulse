using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules;
using Mulse.Modules.Security;
using Service;

namespace Service.Tests;

public sealed class ModulePackageLifecycleTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-package-tests-" + Guid.NewGuid());
    private readonly List<WeakReference> _unloadingContexts = [];
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging()
        .AddMulsePlatform(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), AppContext.BaseDirectory)
        .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeHostEnvironment())
        .AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState()))
        .BuildServiceProvider();

    private RuntimeConfigurationStore Store() => new(Options.Create(new MulseOptions
    {
        RuntimeStatePath = Path.Combine(_directory, "state.json"), PluginDirectories = ["plugins"]
    }), new FakeHostEnvironment { ContentRootPath = _directory }, NoOpRuntimeStatePayloadProtector.Instance);

    private ModuleCatalog Catalog(RuntimeConfigurationStore store) => new(_services, store,
        new FakeHostEnvironment { ContentRootPath = _directory }, TimeProvider.System, NullLogger<ModuleCatalog>.Instance);

    [Fact]
    public async Task BuiltInPackages_CannotBeOverwrittenOrDisabledAndStateRemainsUnchanged()
    {
        var store = Store();
        var catalog = Catalog(store);
        var builtin = catalog.GetPackages().First(package => package.SourceKind == RuntimePackageSourceKind.BuiltIn);
        foreach (var enabled in new[] { false, true })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
            {
                Id = builtin.Id, AssemblyPath = builtin.AssemblyPath, Enabled = enabled
            }, CancellationToken.None));
        }
        Assert.Empty(store.GetState().ManagedPackages);
        Assert.True(catalog.GetPackages().Single(package => package.Id == builtin.Id).IsLoaded);
    }

    [Fact]
    public async Task DisabledPackage_StaysRegisteredAcrossSynchronizationAndRestart()
    {
        var store = Store();
        var catalog = Catalog(store);
        var result = await catalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "offline", AssemblyPath = "C:\\missing\\offline.dll", Enabled = false
        }, CancellationToken.None);
        Assert.False(result.IsLoaded);
        await catalog.SynchronizeAsync(CancellationToken.None);
        var restartedStore = Store();
        var restartedCatalog = Catalog(restartedStore);
        await restartedCatalog.SynchronizeAsync(CancellationToken.None);
        Assert.False(restartedCatalog.GetPackages().Single(package => package.Id == "offline").IsLoaded);
        Assert.False(restartedStore.GetState().ManagedPackages.Single().Enabled);
    }

    [Fact]
    public async Task FailedLoad_DoesNotPersistEnabledRegistration()
    {
        var store = Store();
        var catalog = Catalog(store);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "missing", AssemblyPath = "C:\\missing\\plugin.dll", Enabled = true
        }, CancellationToken.None));
        Assert.Empty(Store().GetState().ManagedPackages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiscoveryDuringExecution_ListsOnlyCompatiblePackagesAsUnloaded(bool subdirectory)
    {
        var store = Store();
        var catalog = Catalog(store);
        await catalog.SynchronizeAsync(CancellationToken.None);
        var pluginDirectory = Path.Combine(_directory, "plugins");
        var packageDirectory = subdirectory ? Path.Combine(pluginDirectory, "fixture") : pluginDirectory;
        Directory.CreateDirectory(packageDirectory);
        var assemblyPath = Path.Combine(packageDirectory, "fixture.dll");
        await File.WriteAllTextAsync(assemblyPath, "incomplete upload");
        File.Copy(typeof(NullLogger).Assembly.Location, Path.Combine(pluginDirectory, "ordinary-library.dll"));
        await catalog.SynchronizeAsync(CancellationToken.None);
        Assert.DoesNotContain(catalog.GetPackages(), package => package.Id is "fixture" or "ordinary-library");
        File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, assemblyPath, overwrite: true);
        await catalog.SynchronizeAsync(CancellationToken.None);
        var discovered = Assert.Single(catalog.GetPackages(), package => package.Id == "fixture");
        Assert.False(discovered.IsLoaded);
        Assert.Equal(RuntimePackageSourceKind.AutoDiscovered, discovered.SourceKind);
        Assert.DoesNotContain(catalog.GetAll(), module => module.Descriptor.Id == "lifecycle-test-fetch");
        Assert.Empty(store.GetState().ManagedPackages);
        await catalog.SynchronizeAsync(CancellationToken.None);
        Assert.False(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
        var restarted = Catalog(Store());
        await restarted.SynchronizeAsync(CancellationToken.None);
        Assert.False(restarted.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
        File.Delete(assemblyPath);
        await catalog.SynchronizeAsync(CancellationToken.None);
        Assert.DoesNotContain(catalog.GetPackages(), package => package.Id == "fixture");
    }

    [Fact]
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public async Task ExcelPackage_IsDiscoveredUnloadedAndSuppliesWorkingParseAndRenderModules()
    {
        var pluginDirectory = Path.Combine(_directory, "plugins", "Mulse.Excel");
        Directory.CreateDirectory(pluginDirectory);
        var assemblyPath = Path.Combine(pluginDirectory, "Mulse.Excel.dll");
        File.Copy(typeof(Mulse.Excel.ExcelModuleInstaller).Assembly.Location, assemblyPath);
        var store = Store();
        var catalog = Catalog(store);
        await catalog.SynchronizeAsync(CancellationToken.None);
        Assert.False(catalog.GetPackages().Single(package => package.Id == "Mulse.Excel").IsLoaded);
        await catalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "Mulse.Excel", AssemblyPath = assemblyPath, Enabled = true
        }, CancellationToken.None);
        Assert.Equal(2, catalog.GetPackages().Single(package => package.Id == "Mulse.Excel").ModuleCount);
        var context = new FlowExecutionContext("excel", "test", DateTimeOffset.UtcNow);
        await using (var render = await catalog.LeaseRenderAsync("excel-render", CancellationToken.None))
        await using (var parse = await catalog.LeaseParseAsync("excel-parse", CancellationToken.None))
        {
            var loadContext = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(render.Module.GetType().Assembly)!;
            Assert.True(loadContext.IsCollectible);
            _unloadingContexts.Add(new WeakReference(loadContext));
            Assert.Equal("Mulse.Excel", render.Entry.PackageId);
            var json = new IntegrationBatch([new("rows.json", BinaryData.FromString("[{\"Name\":\"Excel\"}]"), "application/json", new Dictionary<string, string>())]);
            var workbook = await render.Module.RenderAsync(context, json, new ModuleStepDefinition(), CancellationToken.None);
            var parsed = await parse.Module.ParseAsync(context, workbook, new ModuleStepDefinition(), CancellationToken.None);
            Assert.Equal("[{\"Name\":\"Excel\"}]", parsed.Payloads.Single().Content.ToString());
        }
        await catalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "Mulse.Excel", AssemblyPath = assemblyPath, Enabled = false
        }, CancellationToken.None);
        Assert.DoesNotContain(catalog.GetAll(), module => module.PackageId == "Mulse.Excel");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AutoDiscoveredPackage_RejectsUseThenUnloadsDurablyAndCanBeLoadedAgain(bool enabledFlow)
        => await ExercisePackageLifecycleAsync(enabledFlow);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task ExercisePackageLifecycleAsync(bool enabledFlow)
    {
        var pluginDirectory = Path.Combine(_directory, "plugins");
        Directory.CreateDirectory(pluginDirectory);
        var assemblyPath = Path.Combine(pluginDirectory, "fixture.dll");
        File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, assemblyPath);
        var store = Store();
        var catalog = Catalog(store);
        await catalog.SynchronizeAsync(CancellationToken.None);
        Assert.False(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
        Assert.DoesNotContain(catalog.GetAll(), module => module.Descriptor.Id == "lifecycle-test-fetch");
        await catalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "fixture", AssemblyPath = assemblyPath, Enabled = true
        }, CancellationToken.None);
        Assert.True(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
        Assert.Contains(catalog.GetAll(), module => module.Descriptor.Id == "lifecycle-test-fetch");
        var registration = new ManagedModulePackageDefinition { Id = "fixture", AssemblyPath = assemblyPath, Enabled = false };
        await store.UpsertFlowAsync(new PipelineDefinition
        {
            Id = "dependent-flow", Enabled = enabledFlow,
            Fetches = [new FetchNodeDefinition { Id = "f", Fetch = new ModuleStepDefinition { Module = "LIFECYCLE-TEST-FETCH" } }]
        }, CancellationToken.None);
        var referenced = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpsertManagedPackageAsync(registration, CancellationToken.None));
        Assert.Contains("dependent-flow", referenced.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RemoveManagedPackageAsync("fixture", CancellationToken.None));
        Assert.True(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
        Assert.True(Store().GetState().ManagedPackages.Single().Enabled);
        using (catalog.PinFlowModules(store.GetState().Pipelines.Single()))
        {
            await store.DeleteFlowAsync("dependent-flow", CancellationToken.None);
            var running = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpsertManagedPackageAsync(registration, CancellationToken.None));
            Assert.Contains("active module executions", running.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RemoveManagedPackageAsync("fixture", CancellationToken.None));
        }
        using (var lockedState = new FileStream(Path.Combine(_directory, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => catalog.UpsertManagedPackageAsync(registration, CancellationToken.None));
            Assert.Contains(catalog.GetAll(), module => module.Descriptor.Id == "lifecycle-test-fetch");
            Assert.True(store.GetState().ManagedPackages.Single().Enabled);
        }
        await using (var lease = await catalog.LeaseFetchAsync("lifecycle-test-fetch", CancellationToken.None))
        {
            _unloadingContexts.Add(new WeakReference(System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(lease.Module.GetType().Assembly)));
            var active = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpsertManagedPackageAsync(registration, CancellationToken.None));
            Assert.Contains("active module executions", active.Message);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RemoveManagedPackageAsync("fixture", CancellationToken.None));
            Assert.True(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
            Assert.True(Store().GetState().ManagedPackages.Single().Enabled);
        }
        Assert.False((await catalog.UpsertManagedPackageAsync(registration, CancellationToken.None)).IsLoaded);
        Assert.DoesNotContain(catalog.GetAll(), module => module.Descriptor.Id == "lifecycle-test-fetch");
        await catalog.SynchronizeAsync(CancellationToken.None);
        var restartedStore = Store();
        var restartedCatalog = Catalog(restartedStore);
        await restartedCatalog.SynchronizeAsync(CancellationToken.None);
        Assert.False(restartedCatalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
        var loaded = await restartedCatalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "fixture", AssemblyPath = assemblyPath, Enabled = true
        }, CancellationToken.None);
        Assert.True(loaded.IsLoaded);
        Assert.True(Store().GetState().ManagedPackages.Single().Enabled);
        Assert.Contains(restartedCatalog.GetAll(), module => module.Descriptor.Id == "lifecycle-test-fetch");
        await restartedCatalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "fixture", AssemblyPath = assemblyPath, Enabled = false
        }, CancellationToken.None);
    }

    public void Dispose()
    {
        _services.Dispose();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (attempt > 0 && _unloadingContexts.All(context => !context.IsAlive)) break;
            Thread.Sleep(20);
        }
        Assert.All(_unloadingContexts, context => Assert.False(context.IsAlive, "The unloaded plugin context is still rooted."));
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

public sealed class LifecycleTestInstaller : Mulse.Modules.Abstractions.IModuleInstaller
{
    public void Install(Mulse.Modules.Abstractions.IModuleRegistryBuilder builder) => builder.AddFetch<LifecycleTestFetch>();
}

public sealed class LifecycleTestFetch : Mulse.Modules.Abstractions.IFetchModule
{
    public ModuleDescriptor Descriptor { get; } = new("lifecycle-test-fetch", "Lifecycle test", ModuleKind.Fetch, "Test-only plugin module.");

    public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken cancellationToken)
        => Task.FromResult(IntegrationBatch.Empty);
}

