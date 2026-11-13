using Microsoft.Extensions.Options;
using Mulse.Persistence;

namespace Service.Execution;

public sealed class ProcessingOptions
{
    public int ShutdownDrainSeconds { get; set; } = 90;
    public int MaxConcurrentExecutions { get; set; } = 64;
}

public sealed class AdmissionRejectedException(string message) : InvalidOperationException(message);

public sealed record ProcessingStatus(string State, bool MaintenanceRequested, int ActiveExecutions, int ActiveAdmissions,
    bool ShutdownRequested, string? Error);

public sealed class ProcessingCoordinator : IHostedLifecycleService, IDisposable
{
    private readonly object _sync = new();
    private readonly SemaphoreSlim _transition = new(1, 1);
    private readonly IRuntimeConfigurationStore _store;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ProcessingOptions _options;
    private readonly IWorkspaceOwnershipGuard? _ownership;
    private readonly WorkspaceDatabase? _database;
    private readonly ILogger<ProcessingCoordinator> _logger;
    private readonly CancellationTokenSource _abort = new();
    private readonly CancellationTokenRegistration _stoppingRegistration;
    private TaskCompletionSource _drained = CompletedSignal();
    private bool _maintenance;
    private bool _stopping;
    private bool _transitioning;
    private int _executions;
    private int _admissions;
    private string? _error;

    public ProcessingCoordinator(IRuntimeConfigurationStore store, IHostApplicationLifetime lifetime,
        IOptions<ProcessingOptions> options, ILogger<ProcessingCoordinator> logger, IWorkspaceOwnershipGuard? ownership = null, WorkspaceDatabase? database = null)
    {
        _database = database;
        _ownership = ownership;
        _store = store;
        _lifetime = lifetime;
        _options = options.Value;
        _logger = logger;
        _maintenance = store.GetState().MaintenanceRequested;
        _stoppingRegistration = lifetime.ApplicationStopping.Register(() => { lock (_sync) _stopping = true; });
    }

    public CancellationToken ForcedCancellation => _abort.Token;
    public bool IsAccepting { get { lock (_sync) return !_maintenance && !_stopping && !_transitioning && _error is null && _database?.IsMigrationSourceSealed != true; } }
    public bool IsStopping { get { lock (_sync) return _stopping || _lifetime.ApplicationStopping.IsCancellationRequested; } }

    public ProcessingStatus GetStatus()
    {
        lock (_sync)
        {
            var state = _error is not null ? "Faulted" : _stopping ? "Stopping" : _maintenance || _transitioning
                ? _executions + _admissions == 0 ? "Paused" : "Draining" : "Running";
            if (_database?.IsMigrationSourceSealed == true && _error is null && !_stopping)
                return new("MigrationLocked", true, _executions, _admissions, false, "Source sealed for storage migration. Finalize the same migration and restart against the target; this source cannot resume.");
            return new(state, _maintenance, _executions, _admissions, _stopping, _error);
        }
    }

    public void Fail(string reason)
    {
        lock (_sync) _error = reason;
        _abort.Cancel();
    }

    public IDisposable Enter(bool execution)
    {
        try { _ownership?.EnsureAvailable(); }
        catch (Exception exception)
        {
            Fail("Workspace ownership could not be verified. Restart before processing more work.");
            _logger.LogCritical(exception, "Admission blocked because workspace ownership could not be verified.");
            throw;
        }
        lock (_sync)
        {
            if (!IsAccepting) throw new AdmissionRejectedException("Processing is paused, draining or shutting down. No new work was admitted; retry after maintenance.");
            if (execution && _executions >= _options.MaxConcurrentExecutions)
                throw new AdmissionRejectedException("The active execution limit has been reached. Retry later.");
            if (_executions + _admissions == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            if (execution) _executions++; else _admissions++;
            return new Admission(this, execution);
        }
    }

    private void Leave(bool execution)
    {
        lock (_sync)
        {
            if (execution) _executions--; else _admissions--;
            if (_executions + _admissions == 0) _drained.TrySetResult();
        }
    }

    public async Task<ProcessingStatus> SetMaintenanceAsync(bool requested, CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sync)
            {
                if (_stopping) throw new AdmissionRejectedException("The host is shutting down; maintenance state cannot be changed now.");
                if (_database?.IsMigrationSourceSealed == true) throw new AdmissionRejectedException("Migration sealed the source. Restart against its verified target; this source cannot resume.");
                if (_error is not null) throw new AdmissionRejectedException("Processing is faulted. Restart to reload committed state and verify ownership before admitting more work.");
                _transitioning = true;
            }
            try
            {
                await _store.SetMaintenanceAsync(requested, cancellationToken).ConfigureAwait(false);
                lock (_sync) { _maintenance = requested; _transitioning = false; }
                _logger.LogInformation("Operator set persistent maintenance mode to {Requested}. Active work is allowed to drain.", requested);
            }
            catch (Exception exception)
            {
                lock (_sync) { _error = "Maintenance persistence was not confirmed. Processing remains blocked; restart and inspect committed state."; }
                _logger.LogError(exception, "Maintenance transition failed; admission remains closed.");
                throw;
            }
            return GetStatus();
        }
        finally { _transition.Release(); }
    }

    public async Task<IDisposable> EnterMaintenanceOperationAsync(CancellationToken cancellationToken)
    {
        await _transition.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ownership?.EnsureAvailable();
            lock (_sync)
            {
                if (!_maintenance || _stopping || _error is not null || _executions + _admissions != 0)
                    throw new AdmissionRejectedException("Pause processing and wait until all active work has drained before migrating storage.");
                _admissions++;
                _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
                return new MaintenanceOperation(this);
            }
        }
        catch { _transition.Release(); throw; }
    }

    public Task WaitForDrainAsync(CancellationToken cancellationToken)
    {
        lock (_sync) return _drained.Task.WaitAsync(cancellationToken);
    }

    private sealed class MaintenanceOperation(ProcessingCoordinator owner) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            owner.Leave(execution: false);
            owner._transition.Release();
        }
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        lock (_sync) _stopping = true;
        _logger.LogInformation("Shutdown admission closed; draining {Executions} executions and {Admissions} inbound admissions.", GetStatus().ActiveExecutions, GetStatus().ActiveAdmissions);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_options.ShutdownDrainSeconds));
        try
        {
            await WaitForDrainAsync(budget.Token).ConfigureAwait(false);
            _logger.LogInformation("Admitted work finished during graceful shutdown. Persisted pending work remains for restart.");
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            _logger.LogError("Shutdown drain deadline expired; cancelling remaining work. Interrupted effects require recovery/reconciliation; shutdown is not confirmed safe.");
            _abort.Cancel();
        }
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() { _stoppingRegistration.Dispose(); _abort.Dispose(); _transition.Dispose(); }
    private static TaskCompletionSource CompletedSignal()
    {
        var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    private sealed class Admission(ProcessingCoordinator owner, bool execution) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Leave(execution); }
    }
}
