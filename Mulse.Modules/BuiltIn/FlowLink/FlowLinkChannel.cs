using System.Collections.Concurrent;
using Mulse.Modules.Payloads;
using Mulse.Modules.Triggers;

namespace Mulse.Modules.BuiltIn.FlowLink;

/// <summary>
/// An in-process, named hand-off point pairing a <see cref="Deliver.FlowLinkDeliverModule"/> "producer" step in
/// one flow with a <see cref="Fetch.FlowLinkFetchModule"/> "consumer" step in another (or the same) flow, both
/// configured with the same "channel" setting. Delivering to the channel enqueues its batch and wakes every
/// push-triggered subscriber; no external transport (HTTP, file system, queue) is involved, so this only works
/// for flows hosted in the same Mulse process. Conceptually a BizTalk direct-bound send/receive port pair.
/// </summary>
internal sealed class FlowLinkChannel
{
    private static readonly ConcurrentDictionary<string, FlowLinkChannel> Channels = new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentQueue<IntegrationPayload> _pending = new();
    private readonly ConcurrentDictionary<Guid, PushFlowTriggerContext> _subscriptions = new();
    private readonly string _channelName;

    private FlowLinkChannel(string channelName)
    {
        _channelName = channelName;
    }

    public static FlowLinkChannel GetOrCreate(string channelName)
        => Channels.GetOrAdd(channelName, static key => new FlowLinkChannel(key));

    public static bool TryGet(string channelName, out FlowLinkChannel channel)
        => Channels.TryGetValue(channelName, out channel!);

    /// <summary>Enqueues <paramref name="payloads"/> and signals every currently registered subscriber.</summary>
    public void Publish(IReadOnlyList<IntegrationPayload> payloads, string? reason)
    {
        foreach (var payload in payloads)
        {
            _pending.Enqueue(payload);
        }

        foreach (var subscription in _subscriptions.Values)
        {
            subscription.Signal(reason);
        }
    }

    /// <summary>Drains and returns everything currently queued for this channel.</summary>
    public IReadOnlyList<IntegrationPayload> DequeueReady()
    {
        var payloads = new List<IntegrationPayload>();
        while (_pending.TryDequeue(out var payload))
        {
            payloads.Add(payload);
        }

        return payloads;
    }

    public IAsyncDisposable Subscribe(PushFlowTriggerContext context)
    {
        var subscriptionId = Guid.NewGuid();
        _subscriptions[subscriptionId] = context;
        return new Subscription(this, subscriptionId);
    }

    private void Unsubscribe(Guid subscriptionId)
    {
        _subscriptions.TryRemove(subscriptionId, out _);
        if (_subscriptions.IsEmpty && _pending.IsEmpty)
        {
            ((ICollection<KeyValuePair<string, FlowLinkChannel>>)Channels).Remove(
                new KeyValuePair<string, FlowLinkChannel>(_channelName, this));
        }
    }

    private sealed class Subscription(FlowLinkChannel owner, Guid subscriptionId) : IAsyncDisposable
    {
        private bool _disposed;

        public ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                owner.Unsubscribe(subscriptionId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
