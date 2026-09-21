using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Mulse.Modules;

namespace Service;

public sealed class RuntimeConfigurationStore : IRuntimeConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;
    private readonly IRuntimeStatePayloadProtector _protector;
    private RuntimeMulseState _state;

    static RuntimeConfigurationStore()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public RuntimeConfigurationStore(IOptions<MulseOptions> options, IHostEnvironment environment, IRuntimeStatePayloadProtector protector)
    {
        var configuredPath = options.Value.RuntimeStatePath;
        _statePath = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath));
        _protector = protector;
        _state = LoadState(options.Value);
    }

    public RuntimeMulseState GetState()
    {
        return Clone(_state);
    }

    public async Task<RuntimeMulseState> UpsertFlowAsync(PipelineDefinition pipeline, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            var existingIndex = updated.Pipelines.FindIndex(candidate => string.Equals(candidate.Id, pipeline.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                updated.Pipelines[existingIndex] = Clone(pipeline);
            }
            else
            {
                updated.Pipelines.Add(Clone(pipeline));
            }

            updated.Pipelines = updated.Pipelines
                .OrderBy(static candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> DeleteFlowAsync(string flowId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            updated.Pipelines.RemoveAll(candidate => string.Equals(candidate.Id, flowId, StringComparison.OrdinalIgnoreCase));
            updated.OrchestrationCheckpoints.RemoveAll(candidate => string.Equals(candidate.FlowId, flowId, StringComparison.OrdinalIgnoreCase));
            updated.PendingRetries.RemoveAll(candidate => string.Equals(candidate.FlowId, flowId, StringComparison.OrdinalIgnoreCase));
            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> UpsertManagedPackageAsync(ManagedModulePackageDefinition package, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            var existingIndex = updated.ManagedPackages.FindIndex(candidate => string.Equals(candidate.Id, package.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                updated.ManagedPackages[existingIndex] = Clone(package);
            }
            else
            {
                updated.ManagedPackages.Add(Clone(package));
            }

            updated.ManagedPackages = updated.ManagedPackages
                .OrderBy(static candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> DeleteManagedPackageAsync(string packageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            updated.ManagedPackages.RemoveAll(candidate => string.Equals(candidate.Id, packageId, StringComparison.OrdinalIgnoreCase));
            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> UpsertOrchestrationCheckpointAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            var existingIndex = updated.OrchestrationCheckpoints.FindIndex(candidate =>
                string.Equals(candidate.FlowId, checkpoint.FlowId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.CorrelationKey, checkpoint.CorrelationKey, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                updated.OrchestrationCheckpoints[existingIndex] = Clone(checkpoint);
            }
            else
            {
                updated.OrchestrationCheckpoints.Add(Clone(checkpoint));
            }

            updated.OrchestrationCheckpoints = updated.OrchestrationCheckpoints
                .OrderBy(static candidate => candidate.FlowId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.CorrelationKey, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> DeleteOrchestrationCheckpointAsync(string flowId, string correlationKey, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            updated.OrchestrationCheckpoints.RemoveAll(candidate =>
                string.Equals(candidate.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.CorrelationKey, correlationKey, StringComparison.OrdinalIgnoreCase));
            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> UpsertRetryStateAsync(FlowRetryState retryState, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            var existingIndex = updated.PendingRetries.FindIndex(candidate =>
                string.Equals(candidate.FlowId, retryState.FlowId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ExecutionId, retryState.ExecutionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.StageId, retryState.StageId, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                updated.PendingRetries[existingIndex] = Clone(retryState);
            }
            else
            {
                updated.PendingRetries.Add(Clone(retryState));
            }

            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> DeleteRetryStateAsync(string flowId, string executionId, string stageId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            updated.PendingRetries.RemoveAll(candidate =>
                string.Equals(candidate.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.StageId, stageId, StringComparison.OrdinalIgnoreCase));
            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> DeleteAllRetryStateForExecutionAsync(string flowId, string executionId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            updated.PendingRetries.RemoveAll(candidate =>
                string.Equals(candidate.FlowId, flowId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(candidate.ExecutionId, executionId, StringComparison.OrdinalIgnoreCase));
            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> SetConfigValueAsync(string reference, string value, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            updated.ConfigValues[reference] = value;
            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<RuntimeMulseState> DeleteConfigValueAsync(string reference, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            updated.ConfigValues.Remove(reference);
            _state = updated;
            await PersistAsync(cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally
        {
            _gate.Release();
        }
    }

    private RuntimeMulseState LoadState(MulseOptions options)
    {
        if (File.Exists(_statePath))
        {
            var protectedBytes = File.ReadAllBytes(_statePath);
            var jsonBytes = _protector.Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<RuntimeMulseState>(jsonBytes, SerializerOptions)
                ?? CreateSeedState(options);
        }

        var seededState = CreateSeedState(options);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllBytes(_statePath, _protector.Protect(JsonSerializer.SerializeToUtf8Bytes(seededState, SerializerOptions)));
        return seededState;
    }

    private static RuntimeMulseState CreateSeedState(MulseOptions options)
    {
        return new RuntimeMulseState
        {
            PluginDirectories = options.PluginDirectories.ToList(),
            ManagedPackages = [],
            Pipelines = options.Pipelines.Select(Clone).ToList(),
            OrchestrationCheckpoints = [],
            PendingRetries = []
        };
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(_state, SerializerOptions);
        await File.WriteAllBytesAsync(_statePath, _protector.Protect(jsonBytes), cancellationToken).ConfigureAwait(false);
    }

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"Unable to clone '{typeof(T).Name}'.");
    }
}
