using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Mulse.Persistence;

namespace Service.Tests;

public sealed class StorageMigrationSafetyTests
{
    [Fact]
    public async Task SchemaPreparation_IsOptInAndOnlyCreatesAnEmptyTargetSchema()
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var target = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        await source.Store().SetMaintenanceAsync(true, default);
        target.Store();
        using (var context = target.Factory.CreateDbContext()) await context.Database.EnsureDeletedAsync();
        var database = Open(source);
        using var guard = new Guard();
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default));
        var preview = await database.TransferAsync(target.Factory, guard, id, Fingerprint, true, false, null, default);
        using (var context = target.Factory.CreateDbContext())
        {
            Assert.Empty(context.Workspaces);
            Assert.Equal(5, context.Database.GetAppliedMigrations().Count());
        }
        Assert.False(database.IsMigrationSourceSealed);
        Assert.True((await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default)).Completed);
    }

    [Fact]
    public async Task ExistingOldSchema_IsNotUpgradedEvenWhenPreparationWasRequested()
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var target = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        await source.Store().SetMaintenanceAsync(true, default);
        target.Store();
        using var old = target.Factory.CreateDbContext();
        var previous = old.Database.GetAppliedMigrations().ToArray()[^2];
        await old.GetService<IMigrator>().MigrateAsync(previous);
        var database = Open(source);
        using var guard = new Guard();
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.TransferAsync(target.Factory, guard, Guid.NewGuid(), Fingerprint, true, false, null, default));
        Assert.Equal(4, old.Database.GetAppliedMigrations().Count());
        Assert.False(database.IsMigrationSourceSealed);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task MidCopyFailure_RollsBackAllTargetRowsWithoutSealingSource(string provider)
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture(provider);
        using var target = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var store = source.Store();
        await store.UpsertRetryStateAsync(new() { FlowId = "orders", ExecutionId = "run", StageId = "parse:source" }, default);
        await store.SetMaintenanceAsync(true, default);
        target.Store();
        using (var context = target.Factory.CreateDbContext()) await context.Workspaces.ExecuteDeleteAsync();
        var database = Open(source);
        using var guard = new Guard();
        var id = Guid.NewGuid();
        var preview = await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default);
        target.Failure.OnRetryWrite = () => throw new IOException("Simulated target insertion failure before commit.");
        await Assert.ThrowsAsync<IOException>(() => database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default));
        using var after = target.Factory.CreateDbContext();
        Assert.Empty(after.Workspaces);
        Assert.Empty(after.Retries);
        Assert.False(database.IsMigrationSourceSealed);
        Assert.True(source.Store().GetState().MaintenanceRequested);
        target.Failure.OnRetryWrite = null;
        Assert.True((await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default)).Completed);
    }

    [Fact]
    public async Task MultipleBatches_AreCopiedAndVerifiedWithoutDroppingSnapshotHistory()
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var target = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        await source.Store().SetMaintenanceAsync(true, default);
        target.Store();
        using (var context = target.Factory.CreateDbContext()) await context.Workspaces.ExecuteDeleteAsync();
        var database = Open(source);
        await database.OperationsAsync(session =>
        {
            for (var index = 0; index < 405; index++)
                session.Db.Messages.Add(new() { Id = Guid.NewGuid(), State = "Snapshot", Payload = System.Text.Encoding.UTF8.GetBytes("{\"index\":" + index + "}") });
            return Task.FromResult(true);
        }, true, default);
        using var guard = new Guard();
        var id = Guid.NewGuid();
        var preview = await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, false, null, default);
        Assert.Equal(405, Assert.Single(preview.Tables, row => row.Table == "Messages").Rows);
        var migrated = await database.TransferAsync(target.Factory, guard, id, Fingerprint, false, true, preview.SourceVersion, default);
        Assert.Equal(preview.Tables, migrated.Tables);
        using var copied = target.Factory.CreateDbContext();
        Assert.Equal(405, copied.Messages.Count());
    }

    private const string Fingerprint = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static WorkspaceDatabase Open(WorkspacePersistenceTests.DatabaseFixture fixture)
    {
        var database = new WorkspaceDatabase(fixture.Factory, new DatabasePayloadCodec("primary", fixture.KeyVariable), false);
        Assert.NotNull(database.Load());
        return database;
    }
    private sealed class Guard : IWorkspaceOwnershipGuard
    {
        public void EnsureAvailable() { }
        public void Dispose() { }
    }
}
