using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Mulse.Persistence;
using Npgsql;

namespace Service.Execution;

public sealed class WorkspaceOwnership : IWorkspaceOwnershipGuard
{
    private const string Resource = "Mulse.Workspace.Owner";
    private const long AdvisoryKey = 1297435731;
    private readonly object _sync = new();
    private readonly FileStream? _file;
    private readonly DbConnection? _connection;
    private readonly bool _sqlServer;
    private readonly int _backendPid;
    private bool _lost;
    private bool _disposed;

    public WorkspaceOwnership(string dataFile)
    {
        var path = Path.GetFullPath(dataFile) + ".owner.lock";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try { _file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception)
        {
            throw new InvalidOperationException("Another Service may own this workspace, or the ownership lock cannot be opened. Stop the other process and verify file permissions before starting.", exception);
        }
    }

    public WorkspaceOwnership(string connectionString, bool sqlServer)
    {
        _sqlServer = sqlServer;
        _connection = sqlServer
            ? new SqlConnection(new SqlConnectionStringBuilder(connectionString) { Pooling = false, ConnectRetryCount = 0 }.ConnectionString)
            : new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        try
        {
            _connection.Open();
            using var command = _connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = sqlServer
                ? "DECLARE @result int; EXEC @result = sys.sp_getapplock @Resource = '" + Resource + "', @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = 0; SELECT @result;"
                : "SELECT pg_try_advisory_lock(" + AdvisoryKey + ");";
            var result = command.ExecuteScalar();
            if (sqlServer ? Convert.ToInt32(result) < 0 : result is not true)
                throw new InvalidOperationException("Another Service owns this database workspace. Only one Service process may execute work against it.");
            if (!sqlServer)
            {
                command.CommandText = "SELECT pg_backend_pid();";
                _backendPid = Convert.ToInt32(command.ExecuteScalar());
            }
        }
        catch { _connection.Dispose(); throw; }
    }

    public void EnsureAvailable()
    {
        lock (_sync)
        {
            if (_lost || _disposed) throw new InvalidOperationException("Workspace ownership was lost. Processing must stop; restart to acquire exclusive ownership.");
            try
            {
                if (_file is not null)
                {
                    if (!_file.CanWrite) throw new InvalidOperationException("Workspace file ownership is unavailable.");
                    return;
                }
                if (_connection?.State != ConnectionState.Open) throw new InvalidOperationException("Database ownership session closed.");
                using var command = _connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = _sqlServer
                    ? "SELECT APPLOCK_MODE('public', '" + Resource + "', 'Session');"
                    : "SELECT pg_backend_pid() = " + _backendPid + " AND EXISTS (SELECT 1 FROM pg_locks WHERE locktype = 'advisory' AND pid = pg_backend_pid() AND classid = 0 AND objid = " + AdvisoryKey + " AND objsubid = 1 AND granted);";
                var result = command.ExecuteScalar();
                if (_sqlServer ? !Equals(result, "Exclusive") : result is not true)
                    throw new InvalidOperationException("The exclusive database workspace lock is no longer held.");
            }
            catch (Exception exception)
            {
                _lost = true;
                throw new InvalidOperationException("Workspace ownership could not be verified. No reconnect or automatic takeover was attempted; restart before processing more work.", exception);
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _connection?.Dispose();
            _file?.Dispose();
        }
    }
}
