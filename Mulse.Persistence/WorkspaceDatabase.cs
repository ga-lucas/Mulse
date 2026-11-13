using Microsoft.EntityFrameworkCore;

namespace Mulse.Persistence;

public sealed partial class WorkspaceDatabase(IDbContextFactory<MulseDbContext> factory, DatabasePayloadCodec codec, bool applyMigrations, IWorkspaceOwnershipGuard? ownership = null)
{
    private readonly SemaphoreSlim _transactionGate = new(1, 1);
    private Guid? _version;
    private Dictionary<(WorkspaceDocumentKind, string), WorkspaceDocument> _documents = [];
    private bool _encrypted;
    private string? _keyId;
    private bool _loaded;
    private volatile bool _requiresReload;

    public void EnsureAvailable()
    {
        ownership?.EnsureAvailable();
        if (_requiresReload)
            throw new InvalidOperationException("Database state is stale or a commit was not acknowledged. Stop the Service and restart to reload committed state before continuing; do not blindly replay external effects.");
    }

    public WorkspaceDatabaseSnapshot? Load()
    {
        EnsureAvailable();
        using var db = factory.CreateDbContext();
        if (applyMigrations) db.Database.Migrate();
        using var transaction = db.Database.BeginTransaction(System.Data.IsolationLevel.Serializable);
        var workspace = db.Workspaces.AsNoTracking().SingleOrDefault();
        if (workspace is null)
        {
            if (db.Flows.Any() || db.FlowRevisions.Any() || db.ConfigurationValues.Any() || db.ModulePackages.Any() || db.Checkpoints.Any() || db.Retries.Any()
                            || db.Executions.Any() || db.Messages.Any() || db.ExecutionEvents.Any() || db.Deliveries.Any())
                throw new InvalidOperationException("Database contains records without workspace metadata. Restore a consistent database; no seed data was written.");
            _loaded = true;
            return null;
        }
        if (workspace.Id != 1 || workspace.Encrypted != (workspace.KeyId is not null))
            throw new InvalidOperationException("Invalid database workspace metadata. State was not reseeded.");
        if (workspace.StorageMigrationState == "TargetStaged")
            throw new InvalidOperationException("This database is an unfinalized migration target. Reopen the source Service and finalize the same migration before starting against this target.");
        Migration = new(workspace.StorageMigrationId, workspace.StorageMigrationState, workspace.StorageMigrationTarget);
        string Decode(byte[] payload, string identity) => codec.Unprotect(payload, identity, workspace.Encrypted, workspace.KeyId);
        var documents = new List<WorkspaceDocument>();
        foreach (var flow in db.Flows.AsNoTracking().Include(row => row.CurrentRevision))
        {
            if (flow.CurrentRevision.FlowKey != flow.Key)
                throw new InvalidOperationException("A flow points to another flow's revision. No state was loaded.");
            documents.Add(new(WorkspaceDocumentKind.Flow, flow.Key, Decode(flow.CurrentRevision.Definition, RevisionIdentity(flow.CurrentRevision.Id)))
            {
                OriginalId = flow.OriginalId, Name = flow.Name, Category = flow.Category, Enabled = flow.Enabled
            });
        }
        foreach (var row in db.ConfigurationValues.AsNoTracking()) documents.Add(Document(WorkspaceDocumentKind.Configuration, row));
        foreach (var row in db.ModulePackages.AsNoTracking()) documents.Add(Document(WorkspaceDocumentKind.ModulePackage, row));
        foreach (var row in db.Checkpoints.AsNoTracking()) documents.Add(Document(WorkspaceDocumentKind.Checkpoint, row) with { FlowKey = row.FlowKey });
        foreach (var row in db.Retries.AsNoTracking()) documents.Add(Document(WorkspaceDocumentKind.Retry, row) with
        {
            FlowKey = row.FlowKey, ExecutionKey = row.ExecutionKey, NextAttemptUtcTicks = row.NextAttemptUtcTicks, NotificationsExhausted = row.NotificationsExhausted
        });
        var state = new WorkspaceDatabaseState(Decode(workspace.PluginDirectories, "workspace:directories"), documents, workspace.MaintenanceRequested);
        _version = workspace.Version;
        _encrypted = workspace.Encrypted;
        _keyId = workspace.KeyId;
        _documents = documents.ToDictionary(row => (row.Kind, row.Key));
        _loaded = true;
        return new(state, _encrypted, _keyId);

        WorkspaceDocument Document(WorkspaceDocumentKind kind, IPayloadRecord row)
            => new(kind, row.Key, Decode(row.Payload, Identity(kind, row.Key)));
    }

    public async Task SaveAsync(WorkspaceDatabaseState state, bool encrypted, string? importSource, CancellationToken cancellationToken)
    {
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await SaveCoreAsync(state, encrypted, importSource, cancellationToken).ConfigureAwait(false); }
        finally { _transactionGate.Release(); }
    }

    private async Task SaveCoreAsync(WorkspaceDatabaseState state, bool encrypted, string? importSource, CancellationToken cancellationToken)
    {
        EnsureAvailable();
        if (!_loaded) throw new InvalidOperationException("Load the database before committing a workspace.");
        if (encrypted) codec.ValidateKey();
        var incoming = state.Documents.ToDictionary(row => (row.Kind, row.Key));
        await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var workspace = await db.Workspaces.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (workspace?.Version != _version)
        {
            _requiresReload = true;
            throw new DbUpdateConcurrencyException("Another Service changed this workspace. Stop competing instances and restart to reload committed state; no changes were saved.");
        }
        EnsureWritable(workspace);
        var reencrypt = workspace is not null && workspace.Encrypted != encrypted;
        if (workspace is null)
        {
            workspace = new() { ImportSource = importSource };
            db.Workspaces.Add(workspace);
        }
        workspace.Version = Guid.NewGuid();
        workspace.MaintenanceRequested = state.MaintenanceRequested;
        workspace.Encrypted = encrypted;
        workspace.KeyId = encrypted ? codec.KeyId : null;
        workspace.PluginDirectories = codec.Protect(state.PluginDirectoriesJson, "workspace:directories", encrypted);
        // Claim the workspace version inside this transaction before changing any operational records.
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            _requiresReload = true;
            throw;
        }

        if (reencrypt)
        {
            foreach (var revision in await db.FlowRevisions.ToListAsync(cancellationToken).ConfigureAwait(false))
                revision.Definition = codec.Protect(codec.Unprotect(revision.Definition, RevisionIdentity(revision.Id), _encrypted, _keyId), RevisionIdentity(revision.Id), encrypted);
            await ReencryptOperationsAsync(db, encrypted, cancellationToken).ConfigureAwait(false);
        }
        foreach (var old in _documents.Values.Where(row => !incoming.ContainsKey((row.Kind, row.Key))))
            await DeleteAsync(db, old, cancellationToken).ConfigureAwait(false);
        foreach (var document in incoming.Values)
        {
            _documents.TryGetValue((document.Kind, document.Key), out var old);
            if (!reencrypt && document == old) continue;
            if (document.Kind == WorkspaceDocumentKind.Flow)
            {
                var flow = await db.Flows.FindAsync([document.Key], cancellationToken).ConfigureAwait(false);
                if (flow is null)
                {
                    flow = new() { Key = document.Key };
                    db.Flows.Add(flow);
                }
                if (old?.Payload != document.Payload)
                {
                    var last = await db.FlowRevisions.Where(row => row.FlowKey == document.Key).MaxAsync(row => (long?)row.Number, cancellationToken).ConfigureAwait(false) ?? 0;
                    var revision = new FlowRevisionRecord
                    {
                        Id = Guid.NewGuid(), FlowKey = document.Key, Number = checked(last + 1), CreatedUtcTicks = DateTime.UtcNow.Ticks
                    };
                    revision.Definition = codec.Protect(document.Payload, RevisionIdentity(revision.Id), encrypted);
                    db.FlowRevisions.Add(revision);
                    flow.CurrentRevisionId = revision.Id;
                    flow.CurrentRevision = revision;
                }
                flow.OriginalId = document.OriginalId;
                flow.Name = document.Name;
                flow.Category = document.Category;
                flow.Enabled = document.Enabled;
            }
            else
            {
                switch (document.Kind)
                {
                    case WorkspaceDocumentKind.Configuration: await UpsertAsync<ConfigurationRecord>(db, document, encrypted, cancellationToken).ConfigureAwait(false); break;
                    case WorkspaceDocumentKind.ModulePackage: await UpsertAsync<ModulePackageRecord>(db, document, encrypted, cancellationToken).ConfigureAwait(false); break;
                    case WorkspaceDocumentKind.Checkpoint:
                        var checkpoint = await UpsertAsync<CheckpointRecord>(db, document, encrypted, cancellationToken).ConfigureAwait(false);
                        checkpoint.FlowKey = document.FlowKey;
                        break;
                    case WorkspaceDocumentKind.Retry:
                        var retry = await UpsertAsync<RetryRecord>(db, document, encrypted, cancellationToken).ConfigureAwait(false);
                        retry.FlowKey = document.FlowKey;
                        retry.ExecutionKey = document.ExecutionKey;
                        retry.NextAttemptUtcTicks = document.NextAttemptUtcTicks;
                        retry.NotificationsExhausted = document.NotificationsExhausted;
                        break;
                    default: throw new InvalidOperationException("Unknown workspace document kind.");
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _requiresReload = true;
            throw new InvalidOperationException("Database commit was not acknowledged. Stop the Service and restart to reload committed state; do not assume the transaction rolled back or replay external effects.", exception);
        }
        _version = workspace.Version;
        _encrypted = encrypted;
        _keyId = workspace.KeyId;
        _documents = incoming;
    }

    private async Task<T> UpsertAsync<T>(MulseDbContext db, WorkspaceDocument document, bool encrypted, CancellationToken cancellationToken) where T : class, IPayloadRecord, new()
    {
        var row = await db.Set<T>().FindAsync([document.Key], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            row = new() { Key = document.Key };
            db.Set<T>().Add(row);
        }
        row.Payload = codec.Protect(document.Payload, Identity(document.Kind, document.Key), encrypted);
        return row;
    }

    private static async Task DeleteAsync(MulseDbContext db, WorkspaceDocument document, CancellationToken cancellationToken)
    {
        switch (document.Kind)
        {
            case WorkspaceDocumentKind.Flow: await RemoveAsync<FlowRecord>(db, document.Key, cancellationToken).ConfigureAwait(false); break;
            case WorkspaceDocumentKind.Configuration: await RemoveAsync<ConfigurationRecord>(db, document.Key, cancellationToken).ConfigureAwait(false); break;
            case WorkspaceDocumentKind.ModulePackage: await RemoveAsync<ModulePackageRecord>(db, document.Key, cancellationToken).ConfigureAwait(false); break;
            case WorkspaceDocumentKind.Checkpoint: await RemoveAsync<CheckpointRecord>(db, document.Key, cancellationToken).ConfigureAwait(false); break;
            case WorkspaceDocumentKind.Retry: await RemoveAsync<RetryRecord>(db, document.Key, cancellationToken).ConfigureAwait(false); break;
            default: throw new InvalidOperationException("Unknown workspace document kind.");
        }
    }

    private static async Task RemoveAsync<T>(MulseDbContext db, string key, CancellationToken cancellationToken) where T : class
    {
        var row = await db.Set<T>().FindAsync([key], cancellationToken).ConfigureAwait(false)
            ?? throw new DbUpdateConcurrencyException("A committed workspace record was removed externally. Restart to reload state.");
        db.Remove(row);
    }

    private static string Identity(WorkspaceDocumentKind kind, string key) => kind + ":" + key;
    private static string RevisionIdentity(Guid id) => "revision:" + id.ToString("N");
}
