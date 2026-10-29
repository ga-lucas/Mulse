using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules;
using Mulse.Modules.Security;
using Service;
using Service.Models;

namespace Service.Tests;

public sealed class WorkspaceBackupTests : IDisposable
{
    private const string Password = "a-long-backup-password";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-backup-tests-" + Guid.NewGuid());
    private readonly ServiceProvider _services = new ServiceCollection().AddLogging()
        .AddMulsePlatform(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), AppContext.BaseDirectory)
        .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeHostEnvironment())
        .AddSingleton<IRuntimeConfigurationStore>(new FakeRuntimeConfigurationStore(new RuntimeMulseState()))
        .BuildServiceProvider();

    private RuntimeConfigurationStore Store(string name) => new(
        Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, name + ".json"), PluginDirectories = ["local-plugins"] }),
        new FakeHostEnvironment { ContentRootPath = _directory }, NoOpRuntimeStatePayloadProtector.Instance);

    private ModuleCatalog Catalog(RuntimeConfigurationStore store) => new(_services, store,
        new FakeHostEnvironment { ContentRootPath = _directory }, TimeProvider.System, NullLogger<ModuleCatalog>.Instance);

    private static async Task SeedAsync(RuntimeConfigurationStore store, string id)
    {
        await store.UpsertFlowAsync(new PipelineDefinition
        {
            Id = id, Name = "Financial import", Category = "Finance", Enabled = true,
            Fetches = [new FetchNodeDefinition { Id = "f", Fetch = new ModuleStepDefinition { Module = "fetch", Settings = new() { ["path"] = "{{secret:password}}" } } }],
            Parses = [new ParseNodeDefinition { Id = "p", FetchNodeIds = ["f"], Parse = new ModuleStepDefinition { Module = "parse" } }],
            Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "render" }, Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "deliver" } }] }]
        }, CancellationToken.None);
        await store.SetConfigValueAsync("password", "super-secret-value", CancellationToken.None);
        await store.UpsertManagedPackageAsync(new ManagedModulePackageDefinition { Id = "custom", AssemblyPath = "C:\\plugins\\custom.dll", Enabled = true }, CancellationToken.None);
        await store.UpsertRetryStateAsync(new FlowRetryState { FlowId = id, ExecutionId = "e", StageId = "s" }, CancellationToken.None);
        await store.UpsertOrchestrationCheckpointAsync(new FlowOrchestrationCheckpoint { FlowId = id, CorrelationKey = "c" }, CancellationToken.None);
    }

    [Fact]
    public async Task Backup_RoundTripsSecretsCategoriesAndRegistrationsWithoutExecutionState()
    {
        var source = Store("source");
        await SeedAsync(source, "source-flow");
        var backups = new WorkspaceBackupService(source, Catalog(source));
        var bytes = backups.Export(Password);
        Assert.DoesNotContain("super-secret-value", Encoding.UTF8.GetString(bytes));
        Assert.False(bytes.SequenceEqual(backups.Export(Password)));
        var target = Store("target");
        var targetBackups = new WorkspaceBackupService(target, Catalog(target));
        var request = new RestoreWorkspaceRequest(Convert.ToBase64String(bytes), Password, "merge", "RESTORE WORKSPACE");
        var preview = targetBackups.Preview(request);
        Assert.Equal(1, preview.FlowCount);
        Assert.Equal(1, preview.PackageCount);
        Assert.Equal(1, preview.ConfigValueCount);
        Assert.Empty(preview.Conflicts);
        Assert.Empty(target.GetState().Pipelines);
        await targetBackups.RestoreAsync(request, CancellationToken.None);
        var restored = Store("target").GetState();
        Assert.False(Assert.Single(restored.Pipelines).Enabled);
        Assert.Equal("Finance", restored.Pipelines[0].Category);
        Assert.Equal("Financial import", restored.Pipelines[0].Name);
        Assert.Equal("{{secret:password}}", restored.Pipelines[0].Fetches[0].Fetch.Settings["path"]);
        Assert.Equal("super-secret-value", restored.ConfigValues["password"]);
        Assert.False(Assert.Single(restored.ManagedPackages).Enabled);
        Assert.Equal("C:\\plugins\\custom.dll", restored.ManagedPackages[0].AssemblyPath);
        Assert.Empty(restored.PendingRetries);
        Assert.Empty(restored.OrchestrationCheckpoints);
    }

    [Fact]
    public async Task Restore_RejectsWrongPasswordTamperedFilesAndMissingConfirmationWithoutChanges()
    {
        var store = Store("protected");
        await SeedAsync(store, "existing");
        var service = new WorkspaceBackupService(store, Catalog(store));
        var bytes = service.Export(Password);
        var request = new RestoreWorkspaceRequest(Convert.ToBase64String(bytes), Password, "replace", "RESTORE WORKSPACE");
        Assert.Throws<ArgumentException>(() => service.Preview(request with { Password = "incorrect-password" }));
        bytes[^1] ^= 1;
        Assert.Throws<ArgumentException>(() => service.Preview(request with { Content = Convert.ToBase64String(bytes) }));
        Assert.Throws<ArgumentException>(() => service.Preview(request with { Content = "not base64" }));
        Assert.Throws<ArgumentException>(() => service.Preview(request with { Mode = "invalid" }));
        Assert.Throws<ArgumentException>(() => service.Export("short"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RestoreAsync(request with { Confirmation = null }, CancellationToken.None));
        Assert.Equal("existing", Store("protected").GetState().Pipelines.Single().Id);
    }

    [Fact]
    public async Task Merge_ReportsConflictsAndRechecksThemInsideAtomicStoreMutation()
    {
        var store = Store("conflict");
        await SeedAsync(store, "FLOW");
        var service = new WorkspaceBackupService(store, Catalog(store));
        var request = new RestoreWorkspaceRequest(Convert.ToBase64String(service.Export(Password)), Password, "merge", "RESTORE WORKSPACE");
        var preview = service.Preview(request);
        Assert.Equal(3, preview.Conflicts.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RestoreAsync(request, CancellationToken.None));
        Assert.Single(Store("conflict").GetState().Pipelines);
        var incoming = new RuntimeMulseState { Pipelines = [new PipelineDefinition { Id = "flow" }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreWorkspaceAsync(incoming, false, CancellationToken.None));
        Assert.True(Store("conflict").GetState().Pipelines.Single().Enabled);
    }

    [Fact]
    public async Task Replace_RemovesOldWorkspaceAndExecutionStateAndDisablesImportedEntries()
    {
        var source = Store("replace-source");
        await SeedAsync(source, "new");
        var sourceService = new WorkspaceBackupService(source, Catalog(source));
        var target = Store("replace-target");
        await SeedAsync(target, "old");
        await target.SetConfigValueAsync("old-only", "gone", CancellationToken.None);
        var service = new WorkspaceBackupService(target, Catalog(target));
        await service.RestoreAsync(new(Convert.ToBase64String(sourceService.Export(Password)), Password, "replace", "RESTORE WORKSPACE"), CancellationToken.None);
        var state = Store("replace-target").GetState();
        Assert.Equal("new", Assert.Single(state.Pipelines).Id);
        Assert.False(state.Pipelines[0].Enabled);
        Assert.False(state.ManagedPackages[0].Enabled);
        Assert.False(state.ConfigValues.ContainsKey("old-only"));
        Assert.Empty(state.PendingRetries);
        Assert.Empty(state.OrchestrationCheckpoints);
        Assert.Equal(["local-plugins"], state.PluginDirectories);
    }

    [Fact]
    public async Task DeleteAll_ClearsFlowsAndExecutionStateButPreservesConfigAndPackagesAcrossRestart()
    {
        var store = Store("delete");
        await SeedAsync(store, "one");
        await store.UpsertFlowAsync(new PipelineDefinition { Id = "two" }, CancellationToken.None);
        Assert.Equal(2, await store.DeleteAllFlowsAsync(CancellationToken.None));
        var state = Store("delete").GetState();
        Assert.Empty(state.Pipelines);
        Assert.Empty(state.PendingRetries);
        Assert.Empty(state.OrchestrationCheckpoints);
        Assert.Equal("super-secret-value", state.ConfigValues["password"]);
        Assert.Single(state.ManagedPackages);
        Assert.Equal(0, await store.DeleteAllFlowsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CancelledBulkMutation_DoesNotChangeInMemoryOrPersistedState()
    {
        var store = Store("cancel");
        await SeedAsync(store, "retained");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DeleteAllFlowsAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RestoreWorkspaceAsync(new RuntimeMulseState(), true, cancellation.Token));
        Assert.Single(store.GetState().Pipelines);
        Assert.Single(Store("cancel").GetState().Pipelines);
    }

    [Fact]
    public async Task NonconflictingMerge_PreservesExistingFlowsConfigAndExecutionState()
    {
        var source = Store("merge-source");
        await SeedAsync(source, "incoming");
        var target = Store("merge-target");
        await target.UpsertFlowAsync(new PipelineDefinition { Id = "retained", Enabled = true }, CancellationToken.None);
        await target.SetConfigValueAsync("target-only", "keep", CancellationToken.None);
        await target.UpsertRetryStateAsync(new FlowRetryState { FlowId = "retained", ExecutionId = "e", StageId = "s" }, CancellationToken.None);
        await target.UpsertOrchestrationCheckpointAsync(new FlowOrchestrationCheckpoint { FlowId = "retained", CorrelationKey = "c" }, CancellationToken.None);
        var request = new RestoreWorkspaceRequest(Convert.ToBase64String(new WorkspaceBackupService(source, Catalog(source)).Export(Password)), Password, "merge", "RESTORE WORKSPACE");
        var service = new WorkspaceBackupService(target, Catalog(target));
        Assert.Empty(service.Preview(request).Conflicts);
        await service.RestoreAsync(request, CancellationToken.None);
        var state = Store("merge-target").GetState();
        Assert.Equal(2, state.Pipelines.Count);
        Assert.True(state.Pipelines.Single(flow => flow.Id == "retained").Enabled);
        Assert.False(state.Pipelines.Single(flow => flow.Id == "incoming").Enabled);
        Assert.Equal("keep", state.ConfigValues["target-only"]);
        Assert.Equal("super-secret-value", state.ConfigValues["password"]);
        Assert.Equal("retained", Assert.Single(state.PendingRetries).FlowId);
        Assert.Equal("retained", Assert.Single(state.OrchestrationCheckpoints).FlowId);
    }

    [Fact]
    public async Task FailedPersistence_DoesNotPublishBulkChangesOrLeaveTemporaryFiles()
    {
        var store = Store("failure");
        await SeedAsync(store, "retained");
        var path = Path.Combine(_directory, "failure.json");
        using (var lockedFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.DeleteAllFlowsAsync(CancellationToken.None));
            await Assert.ThrowsAnyAsync<IOException>(() => store.RestoreWorkspaceAsync(new RuntimeMulseState(), true, CancellationToken.None));
            Assert.Single(store.GetState().Pipelines);
        }
        Assert.Single(Store("failure").GetState().Pipelines);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData("version")]
    [InlineData("null-node")]
    [InlineData("null-settings")]
    [InlineData("duplicate-flow")]
    [InlineData("duplicate-config")]
    [InlineData("builtin-package")]
    public async Task MalformedBackup_IsRejectedWithoutMutation(string invalidKind)
    {
        var store = Store("invalid");
        await SeedAsync(store, "retained");
        var state = store.GetState();
        var flow = state.Pipelines.Single();
        var document = new WorkspaceBackupDocument(1, DateTimeOffset.UtcNow, state.Pipelines, state.ManagedPackages, state.ConfigValues);
        document = invalidKind switch
        {
            "version" => document with { Version = 99 },
            "null-node" => document with { Flows = [new PipelineDefinition { Id = "invalid", Fetches = [null!], Parses = flow.Parses, Deliveries = flow.Deliveries }] },
            "null-settings" => document with { Flows = [new PipelineDefinition { Id = "invalid", Fetches = [new FetchNodeDefinition { Id = "f", Fetch = new ModuleStepDefinition { Module = "fetch", Settings = null! } }], Parses = flow.Parses, Deliveries = flow.Deliveries }] },
            "duplicate-flow" => document with { Flows = [flow, FlowMappings.CopyPipeline(flow, "RETAINED", false, flow.Category)] },
            "duplicate-config" => document with { ConfigValues = new Dictionary<string, string> { ["Secret"] = "one", ["secret"] = "two" } },
            "builtin-package" => document with { Packages = [new ManagedModulePackageDefinition { Id = "builtin.core", AssemblyPath = "core.dll" }] },
            _ => throw new InvalidOperationException("Unknown test case.")
        };
        var salt = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var key = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), salt, 600_000, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var payload = new AesGcmRuntimeStatePayloadProtector(key).Protect(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(document, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        byte[] bytes = [.. "MULSEBK1"u8, .. salt, .. payload];
        var service = new WorkspaceBackupService(store, Catalog(store));
        Assert.Throws<ArgumentException>(() => service.Preview(new(Convert.ToBase64String(bytes), Password, "replace")));
        Assert.Equal("retained", Store("invalid").GetState().Pipelines.Single().Id);
    }

    public void Dispose()
    {
        _services.Dispose();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

