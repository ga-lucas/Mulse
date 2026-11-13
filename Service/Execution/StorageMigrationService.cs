using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Mulse.Persistence;
using Mulse.Persistence.PostgreSql;
using Mulse.Persistence.Sqlite;
using Mulse.Persistence.SqlServer;
using Npgsql;

namespace Service.Execution;

public sealed record StorageMigrationRequest(string Provider, string ConnectionString, bool PrepareSchema, Guid MigrationId,
    Guid? ExpectedSourceVersion = null, string? Confirmation = null, bool BackupConfirmed = false);
public sealed record StorageMigrationStatus(bool Supported, Guid? MigrationId, string? State, string? TargetFingerprint);

public sealed class StorageMigrationService(ProcessingCoordinator processing, IHostEnvironment environment,
    ILogger<StorageMigrationService> logger, WorkspaceDatabase? database = null)
{
    public StorageMigrationStatus GetStatus() => database is null ? new(false, null, null, null)
        : new(true, database.Migration.Id, database.Migration.State, database.Migration.TargetFingerprint);

    public async Task<StorageMigrationReport> RunAsync(StorageMigrationRequest request, bool execute, CancellationToken token)
    {
        if (database is null) throw new InvalidOperationException("The complete-workspace migration wizard requires database storage. Legacy JSON import remains a separate startup operation.");
        if (request.MigrationId == Guid.Empty) throw new ArgumentException("Supply a nonempty migration ID.");
        if (execute && (request.Confirmation != "MIGRATE STORAGE" || !request.BackupConfirmed || request.ExpectedSourceVersion is null))
            throw new ArgumentException("Preview first, confirm full database/key/plugin backups and type MIGRATE STORAGE.");
        using var operation = await processing.EnterMaintenanceOperationAsync(token).ConfigureAwait(false);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, processing.ForcedCancellation);
        try
        {
            using var target = MigrationTarget.Open(request.Provider, request.ConnectionString, environment.ContentRootPath);
            logger.LogInformation("Storage migration {MigrationId}: {Action} to {Provider}. Connection details are not logged.",
                request.MigrationId, execute ? "copy/finalize" : "preview", target.Provider);
            var result = await database.TransferAsync(target, target.Ownership, request.MigrationId, target.Fingerprint,
                request.PrepareSchema, execute, request.ExpectedSourceVersion, cancellation.Token).ConfigureAwait(false);
            logger.LogInformation("Storage migration {MigrationId}: completed={Completed}, {Rows} records verified. Source stays paused.",
                request.MigrationId, result.Completed, result.Tables.Sum(table => table.Rows));
            return result;
        }
        catch (Exception exception) when (exception is DbException or DbUpdateException or CryptographicException or IOException)
        {
            logger.LogError("Storage migration {MigrationId} failed ({ExceptionType}). No automatic switch or replay occurred. Re-preview the same ID/destination to inspect or finalize committed state.",
                request.MigrationId, exception.GetType().Name);
            throw new InvalidOperationException("Storage migration could not be verified. Check database connectivity, schema permissions and the original encryption key. Keep processing paused. Re-preview the same migration ID and destination; do not assume an interrupted commit rolled back.");
        }
    }

    private sealed class MigrationTarget : IDbContextFactory<MulseDbContext>, IDisposable
    {
        private readonly Func<MulseDbContext> _create;
        public string Provider { get; }
        public string Fingerprint { get; }
        public IWorkspaceOwnershipGuard Ownership { get; }
        private MigrationTarget(string provider, Func<MulseDbContext> create, IWorkspaceOwnershipGuard ownership)
        {
            _create = create;
            Provider = provider;
            Ownership = ownership;
            try
            {
                using var context = create();
                var connection = context.Database.GetDbConnection();
                Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    System.Text.Json.JsonSerializer.Serialize(new[] { provider, connection.DataSource, connection.Database }))));
            }
            catch
            {
                ownership.Dispose();
                throw;
            }
        }

        public static MigrationTarget Open(string provider, string connection, string contentRoot)
        {
            if (string.IsNullOrWhiteSpace(connection) || connection.Length > 16384) throw new ArgumentException("Supply a target connection string of 1-16384 characters.");
            try
            {
                if (string.Equals(provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new SqliteConnectionStringBuilder(connection);
                    if (string.IsNullOrWhiteSpace(builder.DataSource) || builder.DataSource == ":memory:" || builder.Mode == SqliteOpenMode.Memory)
                        throw new ArgumentException("SQLite migration requires a durable local database file.");
                    builder.DataSource = Path.GetFullPath(builder.DataSource, contentRoot);
                    builder.DefaultTimeout = 30;
                    builder.Pooling = false;
                    var options = new DbContextOptionsBuilder<SqliteMulseDbContext>();
                    SqlitePersistence.Configure(options, builder.ToString());
                    return new("Sqlite", () => new SqliteMulseDbContext(options.Options), new WorkspaceOwnership(builder.DataSource));
                }
                if (string.Equals(provider, "SqlServer", StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new SqlConnectionStringBuilder(connection) { Pooling = false, ConnectRetryCount = 0 };
                    if (string.IsNullOrWhiteSpace(builder.InitialCatalog)) throw new ArgumentException("Specify an existing target database.");
                    var options = new DbContextOptionsBuilder<SqlServerMulseDbContext>().UseSqlServer(builder.ToString()).Options;
                    return new("SqlServer", () => new SqlServerMulseDbContext(options), new WorkspaceOwnership(builder.ToString(), true));
                }
                if (string.Equals(provider, "PostgreSql", StringComparison.OrdinalIgnoreCase))
                {
                    var builder = new NpgsqlConnectionStringBuilder(connection) { Pooling = false };
                    if (string.IsNullOrWhiteSpace(builder.Database)) throw new ArgumentException("Specify an existing target database.");
                    var options = new DbContextOptionsBuilder<PostgreSqlMulseDbContext>().UseNpgsql(builder.ToString()).Options;
                    return new("PostgreSql", () => new PostgreSqlMulseDbContext(options), new WorkspaceOwnership(builder.ToString(), false));
                }
                throw new ArgumentException("Choose Sqlite, SqlServer or PostgreSql. Migration to legacy JSON is not supported.");
            }
            catch (ArgumentException)
            {
                throw new ArgumentException("Invalid migration target. Choose Sqlite with a durable local file, or SqlServer/PostgreSql with an explicit existing database and a valid connection string.");
            }
        }

        public MulseDbContext CreateDbContext() => _create();
        public void Dispose() => Ownership.Dispose();
    }
}
