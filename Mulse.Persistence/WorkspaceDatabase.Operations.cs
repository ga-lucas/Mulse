using Microsoft.EntityFrameworkCore;

namespace Mulse.Persistence;

public sealed partial class WorkspaceDatabase
{
    public async Task<T> OperationsAsync<T>(Func<OperationalSession, Task<T>> action, bool write, CancellationToken cancellationToken)
    {
        await _transactionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureAvailable();
            if (!_loaded || _version is null) throw new InvalidOperationException("Initialize the workspace before accessing execution storage.");
            await using var db = await factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            var workspace = await db.Workspaces.SingleAsync(cancellationToken).ConfigureAwait(false);
            if (workspace.Version != _version)
            {
                _requiresReload = true;
                throw new DbUpdateConcurrencyException("Workspace changed in another process. Restart before processing more work.");
            }
            if (write)
            {
                workspace.Version = Guid.NewGuid();
                try { await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false); }
                catch (DbUpdateConcurrencyException) { _requiresReload = true; throw; }
            }
            var result = await action(new(db, codec, workspace.Encrypted, workspace.KeyId)).ConfigureAwait(false);
            if (!write) return result;
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            try { await transaction.CommitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception)
            {
                _requiresReload = true;
                throw new InvalidOperationException("Execution storage commit was not acknowledged. Restart and inspect committed state; do not blindly replay deliveries.", exception);
            }
            _version = workspace.Version;
            return result;
        }
        finally { _transactionGate.Release(); }
    }

    private async Task ReencryptOperationsAsync(MulseDbContext db, bool encrypted, CancellationToken cancellationToken)
    {
        byte[] Rewrite(byte[] payload, string identity)
            => codec.Protect(codec.Unprotect(payload, identity, _encrypted, _keyId), identity, encrypted);
        foreach (var row in await db.Executions.ToListAsync(cancellationToken).ConfigureAwait(false))
            row.Definition = Rewrite(row.Definition, "execution:" + row.Key);
        foreach (var row in await db.ExecutionEvents.ToListAsync(cancellationToken).ConfigureAwait(false))
            row.Detail = Rewrite(row.Detail, "event:" + row.Id.ToString("N"));
        foreach (var row in await db.Messages.ToListAsync(cancellationToken).ConfigureAwait(false))
            row.Payload = Rewrite(row.Payload, "message:" + row.Id.ToString("N"));
        foreach (var row in await db.Deliveries.ToListAsync(cancellationToken).ConfigureAwait(false))
            row.Payload = Rewrite(row.Payload, "delivery:" + row.Key);
    }
}

public sealed class OperationalSession(MulseDbContext db, DatabasePayloadCodec codec, bool encrypted, string? keyId)
{
    public MulseDbContext Db { get; } = db;
    public byte[] Encode(string value, string identity) => codec.Protect(value, identity, encrypted);
    public string Decode(byte[] value, string identity) => codec.Unprotect(value, identity, encrypted, keyId);
}
