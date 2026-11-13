using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Mulse.Modules.Security;
using Mulse.Persistence;

namespace Service.Tests;

public sealed class StorageMigrationTests
{
    public static IEnumerable<object[]> ProviderPairs()
        => from source in WorkspacePersistenceTests.Providers()
           from target in WorkspacePersistenceTests.Providers()
           from encrypted in new[] { false, true }
           select new object[] { source[0], target[0], encrypted };

    [Theory]
    [MemberData(nameof(ProviderPairs))]
    public async Task CompleteWorkspace_MigratesEveryTableWithExactBytesAndRetiresOnlySource(string sourceProvider, string targetProvider, bool encrypted)
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture(sourceProvider);
        using var target = new WorkspacePersistenceTests.DatabaseFixture(targetProvider);
        await SeedAsync(source, encrypted);
        await EmptyTargetAsync(target);
        var database = Open(source);
        using var guard = new TestGuard();
        var id = Guid.NewGuid();
        var preview = await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default);
        Assert.False(preview.Completed);
        Assert.Equal(11, preview.Tables.Count);
        Assert.All(preview.Tables, table => Assert.True(table.Rows > 0, table.Table));
        using (var context = target.Factory.CreateDbContext()) Assert.Empty(context.Workspaces);
        var migrated = await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default);
        Assert.True(migrated.Completed);
        Assert.Equal(preview.Tables, migrated.Tables);
        Assert.True(database.IsMigrationSourceSealed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.OperationsAsync(_ => Task.FromResult(true), true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.Store().SetConfigValueAsync("blocked", "cannot-write", default));
        var again = await Open(source).TransferAsync(target.Factory, guard, id, Fingerprint, false, true, migrated.SourceVersion, default);
        Assert.Equal(migrated.Tables, again.Tables);
        var loaded = new WorkspaceDatabase(target.Factory, Codec(source), false).Load()!;
        Assert.True(loaded.State.MaintenanceRequested);
        Assert.Equal(encrypted, loaded.Encrypted);
        using var configuration = System.Text.Json.JsonDocument.Parse(Assert.Single(loaded.State.Documents, document => document.Kind == WorkspaceDocumentKind.Configuration).Payload);
        Assert.Equal("private-configuration", configuration.RootElement.GetProperty("value").GetString());
        using var copied = target.Factory.CreateDbContext();
        Assert.Equal("TargetReady", Assert.Single(copied.Workspaces).StorageMigrationState);
        Assert.True(Assert.Single(copied.Messages).RetainForConfirmation);
        Assert.Equal("Unknown", Assert.Single(copied.Deliveries).State);
        Assert.Equal(2, copied.FlowRevisions.Count());
        Assert.Single(copied.Retries);
        Assert.Single(copied.Checkpoints);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task LostSourceSealAcknowledgement_IsRecoveredBySameMigrationAfterRestart(string provider)
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture(provider);
        using var target = new WorkspacePersistenceTests.DatabaseFixture(provider);
        await SeedAsync(source, true);
        await EmptyTargetAsync(target);
        var database = Open(source);
        using var guard = new TestGuard();
        var id = Guid.NewGuid();
        var preview = await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default);
        source.CommitFailure.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default));
        Assert.Throws<InvalidOperationException>(() => database.EnsureAvailable());
        Assert.Throws<InvalidOperationException>(() => new WorkspaceDatabase(target.Factory, Codec(source), false).Load());
        source.CommitFailure.Fail = false;
        var restarted = Open(source);
        Assert.True(restarted.IsMigrationSourceSealed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.TransferAsync(target.Factory, guard, Guid.NewGuid(), Fingerprint, false, true, preview.SourceVersion, default));
        Assert.True((await restarted.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default)).Completed);
        Assert.NotNull(new WorkspaceDatabase(target.Factory, Codec(source), false).Load());
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task LostTargetCommitAcknowledgement_LeavesSourcePausedAndTargetUnstartableUntilVerified(string provider)
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture(provider);
        using var target = new WorkspacePersistenceTests.DatabaseFixture(provider);
        await SeedAsync(source, false);
        await EmptyTargetAsync(target);
        var database = Open(source);
        using var guard = new TestGuard();
        var id = Guid.NewGuid();
        var preview = await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default);
        target.CommitFailure.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default));
        Assert.False(database.IsMigrationSourceSealed);
        Assert.Throws<InvalidOperationException>(() => new WorkspaceDatabase(target.Factory, Codec(source), false).Load());
        target.CommitFailure.Fail = false;
        Assert.True((await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default)).Completed);
    }

    [Fact]
    public async Task RejectedOrTamperedTarget_NeverSealsOrChangesTheSource()
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var target = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        await SeedAsync(source, false);
        target.Store();
        var database = Open(source);
        using var guard = new TestGuard();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default));
        Assert.False(database.IsMigrationSourceSealed);
        await EmptyTargetAsync(target);
        var preview = await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default);
        target.CommitFailure.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default));
        target.CommitFailure.Fail = false;
        using (var db = target.Factory.CreateDbContext())
        {
            var flow = Assert.Single(db.Flows);
            flow.Name = "tampered-after-interruption";
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default));
        Assert.False(database.IsMigrationSourceSealed);
        Assert.Equal("Edited", source.Store().GetState().Pipelines.Single().Name);
    }

    [Fact]
    public async Task StalePreviewMissingPauseAndDamagedEncryption_AreRejected()
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var target = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        await SeedAsync(source, true);
        await EmptyTargetAsync(target);
        using var guard = new TestGuard();
        var id = Guid.NewGuid();
        var first = Open(source);
        var preview = await first.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default);
        await source.Store().SetConfigValueAsync("other", "change", default);
        var updated = Open(source);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updated.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default));
        await source.Store().SetMaintenanceAsync(false, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Open(source).TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default));
        await source.Store().SetMaintenanceAsync(true, default);
        using (var context = source.Factory.CreateDbContext())
        {
            var message = Assert.Single(context.Messages);
            var damaged = message.Payload.ToArray();
            damaged[^1] ^= 0x7f;
            message.Payload = damaged;
            await context.SaveChangesAsync();
        }
        await Assert.ThrowsAnyAsync<System.Security.Cryptography.CryptographicException>(() => Open(source).TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default));
        using var destination = target.Factory.CreateDbContext();
        Assert.Empty(destination.Workspaces);
    }

    private const string Fingerprint = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static DatabasePayloadCodec Codec(WorkspacePersistenceTests.DatabaseFixture source) => new("primary", source.KeyVariable);
    private static WorkspaceDatabase Open(WorkspacePersistenceTests.DatabaseFixture fixture)
    {
        var database = new WorkspaceDatabase(fixture.Factory, Codec(fixture), false);
        Assert.NotNull(database.Load());
        return database;
    }
    private static async Task EmptyTargetAsync(WorkspacePersistenceTests.DatabaseFixture target)
    {
        target.Store();
        using var db = target.Factory.CreateDbContext();
        await db.Workspaces.ExecuteDeleteAsync();
    }
    private static async Task SeedAsync(WorkspacePersistenceTests.DatabaseFixture source, bool encrypted)
    {
        var (store, journal) = source.Journal(encrypted);
        var flow = new PipelineDefinition { Id = "orders", Name = "Original", Fetches = [new() { Id = "source", Fetch = new() { Module = "source" } }] };
        await store.UpsertFlowAsync(flow, default);
        await store.UpsertFlowAsync(new() { Id = flow.Id, Name = "Edited", Fetches = flow.Fetches }, default);
        await store.SetConfigValueAsync("credential", "private-configuration", default);
        await store.UpsertManagedPackageAsync(new() { Id = "plugin", AssemblyPath = "C:\\plugins\\kept.dll", Enabled = false }, default);
        await store.UpsertOrchestrationCheckpointAsync(new() { FlowId = flow.Id, CorrelationKey = "tenant", Bookmark = "waiting" }, default);
        await store.UpsertRetryStateAsync(new() { FlowId = flow.Id, ExecutionId = "run", StageId = "deliver:0:0", PipelineSnapshot = flow }, default);
        var execution = new FlowExecutionContext(flow.Id, "run", DateTimeOffset.UtcNow);
        await journal.BeginAsync(flow, execution, default);
        await journal.StageAsync(execution, "parse:source", "Succeeded", IntegrationBatch.Empty, "private-history", default);
        var database = Open(source);
        await database.OperationsAsync(session =>
        {
            var key = WorkspaceDocument.Identity("run");
            var messageId = Guid.NewGuid();
            session.Db.Messages.Add(new() { Id = messageId, Channel = "flow-link:orders", ChannelKey = WorkspaceDocument.Identity("flow-link:orders"), State = "Held", RetainForConfirmation = true,
                ExecutionKey = key, ExecutionId = "run", Payload = session.Encode("{\"message\":\"private-body\"}", "message:" + messageId.ToString("N")) });
            var deliveryKey = WorkspaceDocument.Identity("delivery");
            session.Db.Deliveries.Add(new() { Key = deliveryKey, Id = Guid.NewGuid(), ExecutionKey = key, Stage = "deliver:0:0", State = "Unknown",
                Payload = session.Encode("{\"delivery\":\"private-body\"}", "delivery:" + deliveryKey) });
            return Task.FromResult(true);
        }, true, default);
        await source.Store().SetMaintenanceAsync(true, default);
    }
    private sealed class TestGuard : IWorkspaceOwnershipGuard
    {
        public void EnsureAvailable() { }
        public void Dispose() { }
    }
}
