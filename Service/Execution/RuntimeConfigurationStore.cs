using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Mulse.Modules;

namespace Service.Execution;

public sealed class RuntimeConfigurationStore : IRuntimeConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _statePath;
    private readonly string _keyEnvironmentVariable;
    private IRuntimeStatePayloadProtector _protector;
    private bool _usesEnvelope;
    private RuntimeMulseState _state;

    private const string EnvelopeFormat = "mulse-runtime-state-v1";
    private sealed record StateEnvelope(string Format, bool Encrypted, byte[] Payload);

    static RuntimeConfigurationStore()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public RuntimeConfigurationStore(IOptions<MulseOptions> options, IHostEnvironment environment, IRuntimeStatePayloadProtector? protector = null)
    {
        var configuredPath = options.Value.RuntimeStatePath;
        _statePath = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath));
        _protector = protector ?? NoOpRuntimeStatePayloadProtector.Instance;
        _keyEnvironmentVariable = options.Value.Encryption.KeyEnvironmentVariable;
        _state = LoadState(options.Value, protector);
    }

    public RuntimeMulseState GetState()
    {
        return Clone(_state);
    }

    public RuntimeEncryptionStatus GetEncryptionStatus()
    {
        try
        {
            RuntimeEncryptionKey.FromEnvironment(_keyEnvironmentVariable);
            return new(_protector is AesGcmRuntimeStatePayloadProtector, true, _keyEnvironmentVariable, null);
        }
        catch (InvalidOperationException exception)
        {
            return new(_protector is AesGcmRuntimeStatePayloadProtector, false, _keyEnvironmentVariable, exception.Message);
        }
    }

    public async Task<RuntimeEncryptionStatus> SetEncryptionAsync(bool enabled, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var environmentProtector = RuntimeEncryptionKey.FromEnvironment(_keyEnvironmentVariable);
            IRuntimeStatePayloadProtector next = enabled ? environmentProtector : NoOpRuntimeStatePayloadProtector.Instance;
            await WriteStateAsync(_state, next, true, cancellationToken).ConfigureAwait(false);
            _protector = next;
            _usesEnvelope = true;
            return GetEncryptionStatus();
        }
        finally { _gate.Release(); }
    }

    public async Task<RuntimeMulseState> RestoreWorkspaceAsync(RuntimeMulseState restored, bool replace, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            if (replace)
            {
                updated.Pipelines.Clear();
                updated.ManagedPackages.Clear();
                updated.ConfigValues.Clear();
                updated.OrchestrationCheckpoints.Clear();
                updated.PendingRetries.Clear();
            }
            else if (restored.Pipelines.Any(flow => updated.Pipelines.Any(existing => string.Equals(existing.Id, flow.Id, StringComparison.OrdinalIgnoreCase)))
                || restored.ManagedPackages.Any(package => updated.ManagedPackages.Any(existing => string.Equals(existing.Id, package.Id, StringComparison.OrdinalIgnoreCase)))
                || restored.ConfigValues.Keys.Any(updated.ConfigValues.ContainsKey))
            {
                throw new InvalidOperationException("Merge conflicts with existing flow IDs, package IDs, or configuration keys. Nothing was restored.");
            }
            updated.Pipelines.AddRange(Clone(restored.Pipelines));
            updated.ManagedPackages.AddRange(Clone(restored.ManagedPackages));
            foreach (var pair in restored.ConfigValues) updated.ConfigValues.Add(pair.Key, pair.Value);
            await CommitWorkspaceAsync(updated, cancellationToken).ConfigureAwait(false);
            return Clone(_state);
        }
        finally { _gate.Release(); }
    }

    public async Task<int> DeleteAllFlowsAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var updated = Clone(_state);
            var count = updated.Pipelines.Count;
            updated.Pipelines.Clear();
            updated.OrchestrationCheckpoints.Clear();
            updated.PendingRetries.Clear();
            await CommitWorkspaceAsync(updated, cancellationToken).ConfigureAwait(false);
            return count;
        }
        finally { _gate.Release(); }
    }

    private async Task CommitWorkspaceAsync(RuntimeMulseState updated, CancellationToken cancellationToken)
    {
        await WriteStateAsync(updated, _protector, _usesEnvelope, cancellationToken).ConfigureAwait(false);
        _state = updated;
    }

    private async Task WriteStateAsync(RuntimeMulseState state, IRuntimeStatePayloadProtector protector, bool envelope, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var payload = protector.Protect(JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions));
            var bytes = envelope
                ? JsonSerializer.SerializeToUtf8Bytes(new StateEnvelope(EnvelopeFormat, protector is AesGcmRuntimeStatePayloadProtector, payload), SerializerOptions)
                : payload;
            if (envelope) bytes = [.. "MULSE-STATE\n"u8, .. bytes];
            await File.WriteAllBytesAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Replace(temporaryPath, _statePath, destinationBackupFileName: null);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
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

            await CommitWorkspaceAsync(updated, cancellationToken).ConfigureAwait(false);
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
            await CommitWorkspaceAsync(updated, cancellationToken).ConfigureAwait(false);
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

    private RuntimeMulseState LoadState(MulseOptions options, IRuntimeStatePayloadProtector? initialProtector)
    {
        if (File.Exists(_statePath))
        {
            var protectedBytes = File.ReadAllBytes(_statePath);
            if (protectedBytes.AsSpan().StartsWith("MULSE-STATE\n"u8))
            {
                var envelope = JsonSerializer.Deserialize<StateEnvelope>(protectedBytes.AsSpan("MULSE-STATE\n"u8.Length), SerializerOptions)
                    ?? throw new InvalidOperationException("The runtime state envelope is empty.");
                if (envelope.Format != EnvelopeFormat)
                    throw new InvalidOperationException("Unsupported runtime state format.");
                _protector = envelope.Encrypted
                    ? RuntimeEncryptionKey.FromEnvironment(_keyEnvironmentVariable)
                    : NoOpRuntimeStatePayloadProtector.Instance;
                _usesEnvelope = true;
                return JsonSerializer.Deserialize<RuntimeMulseState>(_protector.Unprotect(envelope.Payload), SerializerOptions)
                    ?? throw new InvalidOperationException("The runtime state is empty.");
            }
            _protector = initialProtector ?? RuntimeEncryptionKey.FromConfiguration(options.Encryption);
            var jsonBytes = _protector.Unprotect(protectedBytes);
            return JsonSerializer.Deserialize<RuntimeMulseState>(jsonBytes, SerializerOptions)
                ?? CreateSeedState(options);
        }

        _protector = initialProtector ?? RuntimeEncryptionKey.FromConfiguration(options.Encryption);
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

    private Task PersistAsync(CancellationToken cancellationToken)
        => WriteStateAsync(_state, _protector, _usesEnvelope, cancellationToken);

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"Unable to clone '{typeof(T).Name}'.");
    }
}
