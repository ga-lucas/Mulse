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
    public async Task Discovery_PrefersCurrentFrameworkAssemblyAndFallsBackToLegacy()
    {
        var packageDirectory = Path.Combine(_directory, "plugins", "fixture");
        var legacyAssemblyPath = Path.Combine(packageDirectory, "fixture.dll");
        var targetFrameworkAssemblyPath = Path.Combine(packageDirectory, "net11.0", "fixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFrameworkAssemblyPath)!);
        File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, legacyAssemblyPath);
        File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, targetFrameworkAssemblyPath);

        var catalog = Catalog(Store());
        await catalog.SynchronizeAsync(CancellationToken.None);
        Assert.Equal(targetFrameworkAssemblyPath, catalog.GetPackages().Single(package => package.Id == "fixture").AssemblyPath);

        File.Delete(targetFrameworkAssemblyPath);
        await catalog.SynchronizeAsync(CancellationToken.None);
        Assert.Equal(legacyAssemblyPath, catalog.GetPackages().Single(package => package.Id == "fixture").AssemblyPath);
    }

    [Fact]
    public async Task ManagedPackageUpsert_UsesCurrentFrameworkAssemblyAndKeepsPersistedPath()
    {
        var packageDirectory = Path.Combine(_directory, "plugins", "fixture");
        var legacyAssemblyPath = Path.Combine(packageDirectory, "fixture.dll");
        var targetFrameworkAssemblyPath = Path.Combine(packageDirectory, "net11.0", "fixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFrameworkAssemblyPath)!);
        File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, targetFrameworkAssemblyPath);

        var store = Store();
        var catalog = Catalog(store);
        var package = await catalog.UpsertManagedPackageAsync(new()
        {
            Id = "fixture", AssemblyPath = legacyAssemblyPath, Enabled = true
        }, CancellationToken.None);
        Assert.Equal(targetFrameworkAssemblyPath, package.AssemblyPath);
        Assert.Equal(legacyAssemblyPath, store.GetState().ManagedPackages.Single().AssemblyPath);

        var restartedCatalog = Catalog(store);
        await restartedCatalog.SynchronizeAsync(CancellationToken.None);
        Assert.Equal(targetFrameworkAssemblyPath, restartedCatalog.GetPackages().Single(candidate => candidate.Id == "fixture").AssemblyPath);
        await restartedCatalog.UpsertManagedPackageAsync(new() { Id = "fixture", AssemblyPath = legacyAssemblyPath, Enabled = false }, CancellationToken.None);
        await catalog.UpsertManagedPackageAsync(new() { Id = "fixture", AssemblyPath = legacyAssemblyPath, Enabled = false }, CancellationToken.None);
    }

    [Fact]
    public async Task ManagedPackageUpsert_DoesNotRewriteCustomAssemblyPaths()
    {
        var customDirectory = Path.Combine(_directory, "user-supplied");
        var assemblyPath = Path.Combine(customDirectory, "fixture.dll");
        var adjacentTargetFrameworkAssemblyPath = Path.Combine(customDirectory, "net11.0", "fixture.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(adjacentTargetFrameworkAssemblyPath)!);
        File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, assemblyPath);
        File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, adjacentTargetFrameworkAssemblyPath);

        var store = Store();
        var catalog = Catalog(store);
        var package = await catalog.UpsertManagedPackageAsync(new()
        {
            Id = "fixture", AssemblyPath = assemblyPath, Enabled = true
        }, CancellationToken.None);
        Assert.Equal(assemblyPath, package.AssemblyPath);
        Assert.Equal(assemblyPath, store.GetState().ManagedPackages.Single().AssemblyPath);
        await catalog.UpsertManagedPackageAsync(new() { Id = "fixture", AssemblyPath = assemblyPath, Enabled = false }, CancellationToken.None);
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
    public async Task ExternalValueTransform_MapsThroughHostInvoker_Reloads_AndProtectsSavedDependencies(bool invoke)
        => await ExerciseExternalTransformAsync(invoke);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private async Task ExerciseExternalTransformAsync(bool invoke)
    {
        var pluginDirectory = Path.Combine(_directory, "plugins", "Mulse.SamplePlugin");
        Directory.CreateDirectory(pluginDirectory);
        var assemblyPath = Path.Combine(pluginDirectory, "Mulse.SamplePlugin.dll");
        File.Copy(typeof(Mulse.SamplePlugin.RegexReplaceTransformModule).Assembly.Location, assemblyPath);
        var store = Store();
        var catalog = Catalog(store);
        await catalog.SynchronizeAsync(default);
        Assert.DoesNotContain(catalog.GetAll(), entry => entry.Descriptor.Id == "regex-replace-transform");
        await catalog.UpsertManagedPackageAsync(new() { Id = "Mulse.SamplePlugin", AssemblyPath = assemblyPath, Enabled = true }, default);
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
            .AddSingleton<Mulse.Modules.Abstractions.IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>()
            .AddSingleton<IModuleInvoker, Service.Modules.ModuleInvoker>().BuildServiceProvider();
        var invoker = services.GetRequiredService<IModuleInvoker>();
        const string mapping = """
            {"fields":[{"kind":"Transform","targetPath":"/code","transformModule":"regex-replace-transform",
            "transformSettings":{"pattern":"[^0-9]","replacement":""},"transformInputs":[{"sourcePath":"/id"}]}]}
            """;
        var context = new FlowExecutionContext("plugin-map", "test", DateTimeOffset.UtcNow);
        try
        {
            if (invoke) Assert.Equal("[{\"code\":\"123\"}]", await Mulse.Modules.Mapping.ParsedDataMapping.MapAsync(mapping, "{\"id\":\"A-123\"}", "Json", default, invoker, context));
            RecordTransformContext(catalog);
            await store.UpsertFlowAsync(new() { Id = "plugin-map", Enabled = false,
                Augments = [new() { Module = "parsed-data-map-augment", Settings = new() { ["mappingJson"] = mapping } }] }, default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpsertManagedPackageAsync(
                new() { Id = "Mulse.SamplePlugin", AssemblyPath = assemblyPath, Enabled = false }, default));
            Assert.Contains("plugin-map", error.Message);
            if (invoke)
            {
                File.SetLastWriteTimeUtc(assemblyPath, File.GetLastWriteTimeUtc(assemblyPath).AddSeconds(2));
                await catalog.ReloadPackageAsync("Mulse.SamplePlugin", default);
                Assert.Equal("[{\"code\":\"45\"}]", await Mulse.Modules.Mapping.ParsedDataMapping.MapAsync(mapping, "{\"id\":\"B-45\"}", "Json", default, invoker, context));
                var dispatch = new Mulse.Modules.BuiltIn.Render.RenderDispatchModule(services.GetRequiredService<Mulse.Modules.Abstractions.IRenderModuleInvoker>());
                var rendered = await dispatch.RenderAsync(context, new([new("order.json",
                    BinaryData.FromString("{\"orderId\":\"O1\",\"lineCount\":1,\"total\":12,\"summarizedAtUtc\":\"2026-01-01T00:00:00Z\"}"),
                    "application/json", new Dictionary<string, string> { ["map"] = "receipt" })]), new()
                    {
                        Settings = new() { ["metadataKey"] = "map", ["casesJson"] = "[{\"value\":\"receipt\",\"module\":\"sample-plugin-typed-render\"}]" }
                    }, default);
                Assert.Contains("Order: O1", rendered.Payloads.Single().GetText());
                var receipt = rendered.Payloads.Single();
                var combined = await dispatch.RenderAsync(context, new([receipt, receipt]), new() { Settings = new()
                {
                    ["metadataKey"] = "map", ["batchMode"] = "PerSelection",
                    ["casesJson"] = "[{\"value\":\"receipt\",\"module\":\"sample-text-batch-render\",\"settings\":{\"header\":\"H\",\"separator\":\"|\",\"trailer\":\"T\"}}]"
                } }, default);
                Assert.Equal("H" + receipt.GetText() + "|" + receipt.GetText() + "T", combined.Payloads.Single().GetText());
                Assert.Equal("2", combined.Payloads.Single().Metadata["batch.count"]);
                RecordTransformContext(catalog);
                await store.UpsertFlowAsync(new() { Id = "plugin-map", Enabled = false, Augments = [new()
                {
                    Module = "map-dispatch-augment", Settings = new() { ["metadataKey"] = "map", ["casesJson"] = "[{\"value\":\"receipt\",\"module\":\"sample-text-batch-render\"}]" }
                }] }, default);
                var rendererDependency = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpsertManagedPackageAsync(
                    new() { Id = "Mulse.SamplePlugin", AssemblyPath = assemblyPath, Enabled = false }, default));
                Assert.Contains("plugin-map", rendererDependency.Message);
            }
        }
        finally
        {
            await store.DeleteFlowAsync("plugin-map", default);
            await catalog.UpsertManagedPackageAsync(new() { Id = "Mulse.SamplePlugin", AssemblyPath = assemblyPath, Enabled = false }, default);
        }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Mulse.Modules.Mapping.ParsedDataMapping.MapAsync(mapping, "{\"id\":\"C\"}", "Json", default, invoker, context));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void RecordTransformContext(IModuleCatalog catalog)
    {
        var lease = catalog.LeaseTransformAsync("regex-replace-transform", default).GetAwaiter().GetResult();
        try
        {
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(lease.Module.GetType().Assembly)!;
            Assert.True(context.IsCollectible);
            _unloadingContexts.Add(new WeakReference(context));
        }
        finally { lease.DisposeAsync().GetAwaiter().GetResult(); }
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSourceReceipts_BlockPackageUnloadEvenWithoutSavedFlowReference(bool checkpoint)
    {
        var pluginDirectory = Path.Combine(_directory, "plugins");
        Directory.CreateDirectory(pluginDirectory);
        var assemblyPath = Path.Combine(pluginDirectory, "fixture.dll");
        File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, assemblyPath);
        var store = Store();
        var catalog = Catalog(store);
        await catalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "fixture", AssemblyPath = assemblyPath, Enabled = true
        }, CancellationToken.None);
        var receipt = new FetchProcessingReceipt("receipt", "fetch", new ModuleStepDefinition { Module = "lifecycle-test-fetch" }, new());
        if (checkpoint)
            await store.UpsertOrchestrationCheckpointAsync(new FlowOrchestrationCheckpoint
            {
                FlowId = "pending", CorrelationKey = "key", SourceReceipts = [receipt]
            }, CancellationToken.None);
        else
            await store.UpsertRetryStateAsync(new FlowRetryState
            {
                FlowId = "pending", ExecutionId = "execution", StageId = "source-outcome", SourceReceipts = [receipt]
            }, CancellationToken.None);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.RemoveManagedPackageAsync("fixture", CancellationToken.None));
        Assert.Contains("pending source outcome receipts", error.Message);
        Assert.True(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
        if (checkpoint) await store.DeleteOrchestrationCheckpointAsync("pending", "key", CancellationToken.None);
        else await store.DeleteAllRetryStateForExecutionAsync("pending", "execution", CancellationToken.None);
        await catalog.UpsertManagedPackageAsync(new ManagedModulePackageDefinition
        {
            Id = "fixture", AssemblyPath = assemblyPath, Enabled = false
        }, CancellationToken.None);
        Assert.False(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
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

