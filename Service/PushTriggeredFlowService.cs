using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Mulse.Modules;

namespace Service;

public sealed class PushTriggeredFlowService(
    IFlowRuntime flowRuntime,
    IModuleCatalog moduleCatalog,
    ILogger<PushTriggeredFlowService> logger) : BackgroundService
{
    private static readonly JsonSerializerOptions SignatureJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Channel<PushFlowTriggerSignal> _signalChannel = Channel.CreateUnbounded<PushFlowTriggerSignal>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<string, byte> _queuedFlowIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _registrationGate = new(1, 1);
    private readonly Dictionary<string, ActivePushRegistration> _activeRegistrations = new(StringComparer.OrdinalIgnoreCase);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await ReconcileRegistrationsAsync(stoppingToken).ConfigureAwait(false);
        var dispatchTask = DispatchSignalsAsync(stoppingToken);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await ReconcileRegistrationsAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _signalChannel.Writer.TryComplete();
            await DisposeRegistrationsAsync().ConfigureAwait(false);
            await dispatchTask.ConfigureAwait(false);
        }
    }

    private async Task DispatchSignalsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var signal in _signalChannel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    logger.LogInformation(
                        "Push trigger received for flow {FlowId} from module {ModuleId}. Reason: {Reason}",
                        signal.FlowId,
                        signal.ModuleId,
                        signal.Reason ?? "n/a");

                    await flowRuntime.ExecuteAsync(signal.FlowId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Push-triggered execution failed for flow {FlowId}.", signal.FlowId);
                }
                finally
                {
                    _queuedFlowIds.TryRemove(signal.FlowId, out _);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReconcileRegistrationsAsync(CancellationToken cancellationToken)
    {
        await _registrationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pushFlows = flowRuntime.GetConfiguredFlows()
                .Where(static flow => flow.Enabled && flow.Trigger.Mode == PipelineTriggerMode.Push)
                .ToArray();
            var activeFlowIds = pushFlows.Select(static flow => flow.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var staleFlowId in _activeRegistrations.Keys.Where(flowId => !activeFlowIds.Contains(flowId)).ToArray())
            {
                await RemoveRegistrationAsync(staleFlowId).ConfigureAwait(false);
            }

            foreach (var flow in pushFlows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var signature = CreateSignature(flow);
                if (_activeRegistrations.TryGetValue(flow.Id, out var existing) && string.Equals(existing.Signature, signature, StringComparison.Ordinal))
                {
                    continue;
                }

                if (existing is not null)
                {
                    await RemoveRegistrationAsync(flow.Id).ConfigureAwait(false);
                }

                await RegisterFlowAsync(flow, signature, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _registrationGate.Release();
        }
    }

    /// <summary>
    /// Registers the flow's push trigger. A flow can declare several sources, so the first source whose fetch
    /// module implements <see cref="IPushTriggeredFetchModule"/> becomes the trigger; the other sources are
    /// resolved normally by the runtime once the trigger fires.
    /// </summary>
    private async Task RegisterFlowAsync(PipelineDefinition flow, string signature, CancellationToken cancellationToken)
    {
        foreach (var source in flow.Sources)
        {
            var fetchLease = await moduleCatalog.LeaseFetchAsync(source.Fetch.Module, cancellationToken).ConfigureAwait(false);
            try
            {
                if (fetchLease.Module is not IPushTriggeredFetchModule pushFetchModule)
                {
                    await fetchLease.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var triggerContext = new PushFlowTriggerContext(flow.Id, source.Fetch.Module, SignalFlow);
                var subscription = await pushFetchModule.RegisterTriggerAsync(triggerContext, source.Fetch, cancellationToken).ConfigureAwait(false);
                _activeRegistrations[flow.Id] = new ActivePushRegistration(flow.Id, signature, fetchLease, subscription);

                logger.LogInformation(
                    "Registered push-triggered flow {FlowId} using source {SourceId} fetch module {ModuleId}.",
                    flow.Id,
                    source.Id,
                    source.Fetch.Module);
                return;
            }
            catch
            {
                await fetchLease.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        logger.LogWarning(
            "Flow {FlowId} uses trigger mode Push but none of its source fetch modules ({ModuleIds}) implement IPushTriggeredFetchModule.",
            flow.Id,
            string.Join(", ", flow.Sources.Select(static source => source.Fetch.Module)));
    }

    private void SignalFlow(PushFlowTriggerSignal signal)
    {
        if (!_queuedFlowIds.TryAdd(signal.FlowId, 0))
        {
            return;
        }

        if (!_signalChannel.Writer.TryWrite(signal))
        {
            _queuedFlowIds.TryRemove(signal.FlowId, out _);
            logger.LogWarning(
                "Unable to queue push trigger for flow {FlowId} from module {ModuleId}.",
                signal.FlowId,
                signal.ModuleId);
        }
    }

    private async Task RemoveRegistrationAsync(string flowId)
    {
        if (_activeRegistrations.Remove(flowId, out var registration))
        {
            await registration.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("Removed push-trigger registration for flow {FlowId}.", flowId);
        }
    }

    private async Task DisposeRegistrationsAsync()
    {
        var registrations = _activeRegistrations.Values.ToArray();
        _activeRegistrations.Clear();
        foreach (var registration in registrations)
        {
            await registration.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string CreateSignature(PipelineDefinition flow)
    {
        var payload = new
        {
            flow.Enabled,
            TriggerMode = flow.Trigger.Mode,
            Sources = flow.Sources.Select(static source => new
            {
                source.Id,
                FetchModule = source.Fetch.Module,
                FetchSettings = source.Fetch.Settings.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            })
        };

        return JsonSerializer.Serialize(payload, SignatureJsonOptions);
    }

    private sealed class ActivePushRegistration : IAsyncDisposable
    {
        public ActivePushRegistration(
            string flowId,
            string signature,
            ModuleLease<IFetchModule> fetchLease,
            IAsyncDisposable subscription)
        {
            FlowId = flowId;
            Signature = signature;
            FetchLease = fetchLease;
            Subscription = subscription;
        }

        public string FlowId { get; }

        public string Signature { get; }

        public ModuleLease<IFetchModule> FetchLease { get; }

        public IAsyncDisposable Subscription { get; }

        public async ValueTask DisposeAsync()
        {
            await Subscription.DisposeAsync().ConfigureAwait(false);
            await FetchLease.DisposeAsync().ConfigureAwait(false);
        }
    }
}
