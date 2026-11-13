using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace Mulse.Persistence;

public sealed record StorageMigrationInfo(Guid? Id, string? State, string? TargetFingerprint);
public sealed record StorageTableVerification(string Table, long Rows, string Sha256);
public sealed record StorageMigrationReport(Guid MigrationId, Guid SourceVersion, bool Completed, bool Encrypted,
    string? KeyId, IReadOnlyList<StorageTableVerification> Tables);

public sealed partial class WorkspaceDatabase
{
    public StorageMigrationInfo Migration { get; private set; } = new(null, null, null);
    public bool IsMigrationSourceSealed => Migration.State == "SourceSealed";

    private static void EnsureWritable(WorkspaceRecord? workspace)
    {
        if (workspace?.StorageMigrationState is "SourceSealed" or "TargetStaged")
            throw new InvalidOperationException("Storage migration has locked this workspace. Finalize the same migration and restart against the verified target; this source cannot be resumed or changed.");
    }

    private static readonly ITransferTable[] TransferTables =
    [
        new TransferTable<WorkspaceRecord>("Workspaces"), new TransferTable<FlowRevisionRecord>("FlowRevisions"), new TransferTable<FlowRecord>("Flows"),
        new TransferTable<ConfigurationRecord>("ConfigurationValues"), new TransferTable<ModulePackageRecord>("ModulePackages"), new TransferTable<CheckpointRecord>("Checkpoints"),
        new TransferTable<RetryRecord>("Retries"), new TransferTable<ExecutionRecord>("Executions"), new TransferTable<ExecutionEventRecord>("ExecutionEvents"),
        new TransferTable<MessageRecord>("Messages"), new TransferTable<DeliveryRecord>("Deliveries")
    ];

    public async Task<StorageMigrationReport> TransferAsync(IDbContextFactory<MulseDbContext> targetFactory,
        IWorkspaceOwnershipGuard targetOwnership, Guid migrationId, string targetFingerprint, bool applyTargetMigrations,
        bool execute, Guid? expectedSourceVersion, CancellationToken token)
    {
        if (migrationId == Guid.Empty) throw new ArgumentException("A nonempty migration ID is required.");
        if (targetFingerprint.Length != 64) throw new ArgumentException("A target identity fingerprint is required.");
        await _transactionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            targetOwnership.EnsureAvailable();
            if (!_loaded || _version is null) throw new InvalidOperationException("Initialize the source workspace first.");
            await using var source = await factory.CreateDbContextAsync(token).ConfigureAwait(false);
            await using var sourceTransaction = await source.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
            var workspace = await source.Workspaces.SingleAsync(token).ConfigureAwait(false);
            if (workspace.Version != _version)
            {
                _requiresReload = true;
                throw new DbUpdateConcurrencyException("Source changed externally. Restart to reload committed state.");
            }
            if (!workspace.MaintenanceRequested) throw new InvalidOperationException("Persist maintenance mode and finish draining before migration.");
            if (execute && expectedSourceVersion != workspace.Version) throw new InvalidOperationException("Source changed after preview. Preview again before migrating.");
            if (workspace.StorageMigrationState == "SourceSealed"
                && (workspace.StorageMigrationId != migrationId || workspace.StorageMigrationTarget != targetFingerprint))
                throw new InvalidOperationException("This source is sealed for a different migration or destination. Use its original migration ID and destination to finalize it.");
            if (source.Model.GetEntityTypes().Count() != TransferTables.Length)
                throw new InvalidOperationException("The migration copier must be updated for this workspace model before transferring data.");
            await using var target = await targetFactory.CreateDbContextAsync(token).ConfigureAwait(false);
            var hasTables = await target.GetService<IRelationalDatabaseCreator>().HasTablesAsync(token).ConfigureAwait(false);
            if (!hasTables && applyTargetMigrations) await target.Database.MigrateAsync(token).ConfigureAwait(false);
            if ((await target.Database.GetPendingMigrationsAsync(token).ConfigureAwait(false)).Any() || target.Database.HasPendingModelChanges())
                throw new InvalidOperationException("Target schema is not current. Allow preparation of an empty database or deploy provider migrations separately. Existing tables are never upgraded by this wizard.");
            await using var targetTransaction = await target.Database.BeginTransactionAsync(IsolationLevel.Serializable, token).ConfigureAwait(false);
            var targetWorkspace = await target.Workspaces.SingleOrDefaultAsync(token).ConfigureAwait(false);
            var existing = targetWorkspace is not null;
            if (existing && (targetWorkspace!.StorageMigrationId != migrationId || targetWorkspace.StorageMigrationTarget != targetFingerprint
                || targetWorkspace.StorageMigrationState is not ("TargetStaged" or "TargetReady")))
                throw new InvalidOperationException("Target contains a workspace. Only an empty target or this exact staged migration is allowed; nothing was overwritten.");
            if (!existing)
            {
                foreach (var table in TransferTables)
                    if (await table.AnyAsync(target, token).ConfigureAwait(false))
                        throw new InvalidOperationException("Target contains workspace records without metadata. No records were overwritten.");
                if (workspace.StorageMigrationState == "SourceSealed")
                    throw new InvalidOperationException("The sealed source requires its original staged target. An empty replacement cannot be substituted.");
            }
            else if (targetWorkspace!.StorageMigrationState == "TargetReady" && workspace.StorageMigrationState != "SourceSealed")
                throw new InvalidOperationException("Target is already finalized but source is not sealed. Investigate these databases before proceeding.");

            var sourceTables = new List<StorageTableVerification>();
            foreach (var table in TransferTables)
            {
                var verification = await table.ReadAsync(source, execute && !existing ? target : null,
                    row => ValidateTransferPayload(row, workspace), migrationId, targetFingerprint, token).ConfigureAwait(false);
                sourceTables.Add(verification);
                if (existing || execute)
                {
                    var copied = await table.ReadAsync(target, null, null, migrationId, targetFingerprint, token).ConfigureAwait(false);
                    if (copied != verification) throw new InvalidOperationException("Target verification failed for " + verification.Table + ". Source remains paused; no effects were replayed.");
                }
            }
            if (!execute) return new(migrationId, workspace.Version, false, workspace.Encrypted, workspace.KeyId, sourceTables);
            targetOwnership.EnsureAvailable();
            ownership?.EnsureAvailable();
            // A staged target cannot start. Seal the source only after the complete target copy is durable.
            await targetTransaction.CommitAsync(token).ConfigureAwait(false);
            if (workspace.StorageMigrationState != "SourceSealed")
            {
                workspace.StorageMigrationId = migrationId;
                workspace.StorageMigrationState = "SourceSealed";
                workspace.StorageMigrationTarget = targetFingerprint;
                await source.SaveChangesAsync(token).ConfigureAwait(false);
                try { await sourceTransaction.CommitAsync(token).ConfigureAwait(false); }
                catch
                {
                    _requiresReload = true;
                    throw;
                }
                Migration = new(migrationId, "SourceSealed", targetFingerprint);
            }
            else await sourceTransaction.CommitAsync(token).ConfigureAwait(false);

            // Finalization is repeatable with the same ID after a crash or a lost commit acknowledgement.
            targetOwnership.EnsureAvailable();
            ownership?.EnsureAvailable();
            target.ChangeTracker.Clear();
            await using var finalize = await target.Database.BeginTransactionAsync(token).ConfigureAwait(false);
            var ready = await target.Workspaces.SingleAsync(token).ConfigureAwait(false);
            ready.StorageMigrationState = "TargetReady";
            await target.SaveChangesAsync(token).ConfigureAwait(false);
            await finalize.CommitAsync(token).ConfigureAwait(false);
            target.ChangeTracker.Clear();
            foreach (var table in TransferTables)
                if (await table.ReadAsync(target, null, null, migrationId, targetFingerprint, token).ConfigureAwait(false)
                    != sourceTables.Single(result => result.Table == table.Name))
                    throw new InvalidOperationException("Post-commit migration validation failed. Keep both workspaces offline for investigation.");
            return new(migrationId, workspace.Version, true, workspace.Encrypted, workspace.KeyId, sourceTables);
        }
        finally { _transactionGate.Release(); }
    }

    private void ValidateTransferPayload(object row, WorkspaceRecord workspace)
    {
        var payload = row switch
        {
            WorkspaceRecord value => (value.PluginDirectories, "workspace:directories"),
            FlowRevisionRecord value => (value.Definition, RevisionIdentity(value.Id)),
            ConfigurationRecord value => (value.Payload, Identity(WorkspaceDocumentKind.Configuration, value.Key)),
            ModulePackageRecord value => (value.Payload, Identity(WorkspaceDocumentKind.ModulePackage, value.Key)),
            CheckpointRecord value => (value.Payload, Identity(WorkspaceDocumentKind.Checkpoint, value.Key)),
            RetryRecord value => (value.Payload, Identity(WorkspaceDocumentKind.Retry, value.Key)),
            ExecutionRecord value => (value.Definition, "execution:" + value.Key),
            ExecutionEventRecord value => (value.Detail, "event:" + value.Id.ToString("N")),
            MessageRecord value => (value.Payload, "message:" + value.Id.ToString("N")),
            DeliveryRecord value => (value.Payload, "delivery:" + value.Key),
            FlowRecord => ((byte[]?, string?))(null, null),
            _ => throw new InvalidOperationException("Unknown transfer entity.")
        };
        if (payload.Item1 is not null) _ = codec.Unprotect(payload.Item1, payload.Item2!, workspace.Encrypted, workspace.KeyId);
    }

    private interface ITransferTable
    {
        string Name { get; }
        Task<bool> AnyAsync(MulseDbContext context, CancellationToken token);
        Task<StorageTableVerification> ReadAsync(MulseDbContext source, MulseDbContext? destination, Action<object>? validate,
            Guid migrationId, string targetFingerprint, CancellationToken token);
    }

    private sealed class TransferTable<T>(string name) : ITransferTable where T : class
    {
        public string Name => name;
        public Task<bool> AnyAsync(MulseDbContext context, CancellationToken token) => context.Set<T>().AnyAsync(token);

        public async Task<StorageTableVerification> ReadAsync(MulseDbContext source, MulseDbContext? destination,
            Action<object>? validate, Guid migrationId, string targetFingerprint, CancellationToken token)
        {
            var properties = source.Model.FindEntityType(typeof(T))!.GetProperties()
                .Where(property => typeof(T) != typeof(WorkspaceRecord) || !property.Name.StartsWith("StorageMigration", StringComparison.Ordinal))
                .OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            var hashes = new List<string>();
            var batch = new List<T>(200);
            await foreach (var row in source.Set<T>().AsNoTracking().AsAsyncEnumerable().WithCancellation(token).ConfigureAwait(false))
            {
                validate?.Invoke(row);
                var values = properties.ToDictionary(property => property.Name, property => property.PropertyInfo!.GetValue(row), StringComparer.Ordinal);
                hashes.Add(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(values))));
                if (destination is null) continue;
                if (row is WorkspaceRecord workspace)
                {
                    workspace.StorageMigrationId = migrationId;
                    workspace.StorageMigrationState = "TargetStaged";
                    workspace.StorageMigrationTarget = targetFingerprint;
                }
                batch.Add(row);
                if (batch.Count == 200) await FlushAsync().ConfigureAwait(false);
            }
            if (batch.Count > 0) await FlushAsync().ConfigureAwait(false);
            hashes.Sort(StringComparer.Ordinal);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var hash in hashes) digest.AppendData(Convert.FromHexString(hash));
            return new(Name, hashes.Count, Convert.ToHexString(digest.GetHashAndReset()));

            async Task FlushAsync()
            {
                destination!.Set<T>().AddRange(batch);
                await destination.SaveChangesAsync(token).ConfigureAwait(false);
                destination.ChangeTracker.Clear();
                batch.Clear();
            }
        }
    }
}
