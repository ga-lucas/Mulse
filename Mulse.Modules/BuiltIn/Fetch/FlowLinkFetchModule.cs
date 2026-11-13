namespace Mulse.Modules.BuiltIn.Fetch;

/// <summary>
/// Push-triggered fetch module that receives the batch delivered by a paired <see cref="Deliver.FlowLinkDeliverModule"/>
/// - in the same flow or a different one - configured with the same "channel" setting. Lets one flow's Deliver
/// step directly hand its rendered result off to another flow's Fetch step, entirely in-process, without an
/// external transport such as HTTP or a file drop. A registered IDurableInbox persists queued batches;
/// without it the legacy process-local channel is used.
/// </summary>
public sealed class FlowLinkFetchModule(IDurableInbox? inbox = null) : IPushTriggeredFetchModule, IFetchOutcomeModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("channel", "Channel", "The channel name this source listens on; must match a flow-link-deliver step's channel elsewhere.", true),
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json, ModuleDataFormat.Xml, ModuleDataFormat.Csv, ModuleDataFormat.Text],
        [ModuleProtocol.Internal],
        [ModuleCapability.Ingestion]);

    public ModuleDescriptor Descriptor { get; } = new(
        "flow-link-fetch",
        "Flow link fetch",
        ModuleKind.Fetch,
        "Receives the batch pushed by a paired flow-link-deliver step, letting one flow's delivery trigger and feed another flow's fetch in-process.",
        SettingDescriptors,
        Recommendation);

    /// <summary><paramref name="input"/> is unused: this is a root source fed by a paired flow-link-deliver step.</summary>
    public async Task<IntegrationBatch> FetchAsync(
        FlowExecutionContext context,
        IntegrationBatch input,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var channelName = ResolveChannel(step);
        if (inbox is not null)
        {
            var batch = await inbox.ClaimAsync("flow-link:" + channelName, context, cancellationToken).ConfigureAwait(false);
            return batch.Count == 0 ? batch : batch with { SourceReceipt = new Dictionary<string, string> { ["channel"] = "flow-link:" + channelName } };
        }
        if (!FlowLinkChannel.TryGet(channelName, out var channel)) return IntegrationBatch.Empty;
        return new IntegrationBatch(channel.DequeueReady());
    }

    public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken cancellationToken)
        => inbox is null ? Task.CompletedTask : inbox.CompleteAsync(context, receipt.Data["channel"], outcome.Succeeded, cancellationToken);

    public Task<IAsyncDisposable> RegisterTriggerAsync(
        PushFlowTriggerContext context,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var channelName = ResolveChannel(step);
        var channel = FlowLinkChannel.GetOrCreate(channelName);
        var subscription = channel.Subscribe(context);
        return Task.FromResult(subscription);
    }

    private static string ResolveChannel(ModuleStepDefinition step)
        => ModuleSettingReader.GetRequired(step.Settings, "channel", "flow-link-fetch");
}
