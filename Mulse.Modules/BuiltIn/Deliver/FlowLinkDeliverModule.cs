namespace Mulse.Modules.BuiltIn.Deliver;

/// <summary>
/// Deliver module that pushes its rendered batch straight into a named in-process channel, waking every
/// <see cref="Fetch.FlowLinkFetchModule"/> source subscribed to that same "channel" setting. Pair this with a
/// flow-link-fetch source (in a different flow, or the same one for a self-feeding loop) to chain flows
/// together without an external transport - e.g. flow A parses and renders an order, then flow B's push-triggered
/// fetch picks it straight up to render a shipping confirmation.
/// </summary>
public sealed class FlowLinkDeliverModule : IDeliverModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("channel", "Channel", "The channel name to publish to; must match a flow-link-fetch source's channel elsewhere.", true),
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.Internal],
        [ModuleCapability.Delivery]);

    public ModuleDescriptor Descriptor { get; } = new(
        "flow-link-deliver",
        "Flow link deliver",
        ModuleKind.Deliver,
        "Pushes the rendered batch into a named in-process channel, triggering and feeding a paired flow-link-fetch source in another flow.",
        SettingDescriptors,
        Recommendation);

    public Task DeliverAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channelName = ModuleSettingReader.GetRequired(step.Settings, "channel", Descriptor.Id);
        if (batch.Payloads.Count == 0)
        {
            return Task.CompletedTask;
        }

        var payloads = batch.Payloads
            .Select(payload => WithProvenance(payload, channelName, context))
            .ToArray();

        var channel = FlowLinkChannel.GetOrCreate(channelName);
        channel.Publish(payloads, $"Flow {context.FlowId} delivered {payloads.Length} payload(s) to channel '{channelName}'.");
        return Task.CompletedTask;
    }

    private static IntegrationPayload WithProvenance(IntegrationPayload payload, string channelName, FlowExecutionContext context)
    {
        var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
        {
            ["flowLinkChannel"] = channelName,
            ["sourceFlowId"] = context.FlowId,
            ["sourceExecutionId"] = context.ExecutionId
        };

        return payload with { Metadata = metadata };
    }
}
