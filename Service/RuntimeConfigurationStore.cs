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
    private RuntimeMulseState _state;

    static RuntimeConfigurationStore()
    {
        SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public RuntimeConfigurationStore(IOptions<MulseOptions> options, IHostEnvironment environment)
    {
        var configuredPath = options.Value.RuntimeStatePath;
        _statePath = Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath));
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

    private RuntimeMulseState LoadState(MulseOptions options)
    {
        if (File.Exists(_statePath))
        {
            var json = File.ReadAllText(_statePath);
            return JsonSerializer.Deserialize<RuntimeMulseState>(json, SerializerOptions)
                ?? CreateSeedState(options);
        }

        var seededState = CreateSeedState(options);
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        File.WriteAllText(_statePath, JsonSerializer.Serialize(seededState, SerializerOptions));
        return seededState;
    }

    private static RuntimeMulseState CreateSeedState(MulseOptions options)
    {
        return new RuntimeMulseState
        {
            PluginDirectories = options.PluginDirectories.ToList(),
            ManagedPackages = [],
            Pipelines = options.Pipelines.Select(Clone).ToList(),
            OrchestrationCheckpoints = []
        };
    }

    private async Task PersistAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        await File.WriteAllTextAsync(_statePath, JsonSerializer.Serialize(_state, SerializerOptions), cancellationToken).ConfigureAwait(false);
    }

    private static T Clone<T>(T value)
    {
        var json = JsonSerializer.Serialize(value, SerializerOptions);
        return JsonSerializer.Deserialize<T>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"Unable to clone '{typeof(T).Name}'.");
    }
}
