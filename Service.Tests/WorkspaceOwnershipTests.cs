using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Mulse.Persistence;

namespace Service.Tests;

public sealed class WorkspaceOwnershipTests
{
    [Fact]
    public void LocalWorkspace_SecondOwnerIsRejectedUntilFirstHandleCloses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-owner-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "workspace.db");
        try
        {
            using (var first = new WorkspaceOwnership(path))
            {
                first.EnsureAvailable();
                Assert.Throws<InvalidOperationException>(() => new WorkspaceOwnership(path));
                first.EnsureAvailable();
            }
            using var replacement = new WorkspaceOwnership(path);
            replacement.EnsureAvailable();
            replacement.Dispose();
            Assert.Throws<InvalidOperationException>(() => replacement.EnsureAvailable());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public void Workspace_HasExclusiveOwnershipAndNeverReacquiresAfterSessionLoss(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        fixture.Store();
        using var db = fixture.Factory.CreateDbContext();
        var connectionString = db.Database.GetConnectionString()!;
        Func<WorkspaceOwnership> acquire = provider == "Sqlite"
            ? () => new(db.Database.GetDbConnection().DataSource)
            : () => new(connectionString, provider == "SqlServer");
        using var first = acquire();
        first.EnsureAvailable();
        Assert.Throws<InvalidOperationException>(() => acquire());
        if (provider != "Sqlite")
        {
            db.Database.OpenConnection();
            using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = provider == "SqlServer"
                ? "SELECT DISTINCT request_session_id FROM sys.dm_tran_locks WHERE resource_type = 'APPLICATION' AND resource_database_id = DB_ID();"
                : "SELECT l.pid FROM pg_locks l JOIN pg_database d ON d.oid = l.database WHERE l.locktype = 'advisory' AND d.datname = current_database() AND l.objid = 1297435731 AND l.granted;";
            var ownerId = Convert.ToInt32(command.ExecuteScalar());
            Assert.True(ownerId > 0);
            command.CommandText = provider == "SqlServer" ? "KILL " + ownerId : "SELECT pg_terminate_backend(" + ownerId + ");";
            command.ExecuteNonQuery();
            Assert.Throws<InvalidOperationException>(() => first.EnsureAvailable());
        }
        first.Dispose();
        using var replacement = acquire();
        replacement.EnsureAvailable();
        Assert.Throws<InvalidOperationException>(() => first.EnsureAvailable());
    }

    [Fact]
    public async Task OwnershipFailure_BlocksCachedReadsAndExecutionAdmission()
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        var store = fixture.Store();
        var guard = new SwitchableGuard();
        var database = new WorkspaceDatabase(fixture.Factory, new DatabasePayloadCodec("primary", fixture.KeyVariable), true, guard);
        Assert.NotNull(database.Load());
        using var lifetime = new ProcessingCoordinatorTests.TestLifetime();
        using var coordinator = new ProcessingCoordinator(store, lifetime, Microsoft.Extensions.Options.Options.Create(new ProcessingOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingCoordinator>.Instance, guard);
        guard.Lost = true;
        Assert.Throws<InvalidOperationException>(() => coordinator.Enter(true));
        Assert.Throws<InvalidOperationException>(() => database.EnsureAvailable());
        coordinator.Fail("Lost workspace ownership.");
        Assert.True(coordinator.ForcedCancellation.IsCancellationRequested);
        Assert.Equal("Faulted", coordinator.GetStatus().State);
        await Assert.ThrowsAsync<AdmissionRejectedException>(() => coordinator.SetMaintenanceAsync(false, default));
    }

    private sealed class SwitchableGuard : IWorkspaceOwnershipGuard
    {
        public bool Lost { get; set; }
        public void EnsureAvailable() { if (Lost) throw new InvalidOperationException("Simulated ownership loss."); }
        public void Dispose() { }
    }
}
