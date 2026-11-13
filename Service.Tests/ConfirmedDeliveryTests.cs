using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules;
using Mulse.Modules.BuiltIn.Deliver;
using Service.ConfigValues;
using Service.Execution;

namespace Service.Tests;

public sealed class ConfirmedDeliveryTests
{
    private static IntegrationBatch Batch() => new([new("body", BinaryData.FromString("one"), "text/plain", new Dictionary<string, string>())]);
    private static PipelineDefinition Pipeline(string module = "confirmed") => new()
    {
        Id = "orders", Enabled = true,
        Fetches = [new() { Id = "f", Fetch = new() { Module = "fetch" } }],
        Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
        Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = module }, RequireConfirmation = true }] }]
    };
    private static FlowRuntime Runtime(PipelineDefinition flow, FakeExecutableModuleCatalog catalog, (RuntimeConfigurationStore Store, FlowJournal Journal) pair)
        => new(new FakeFlowDefinitionService(flow), catalog, new FlowOrchestrationStateStore(pair.Store), new FlowRetryStateStore(pair.Store),
            new ConfigValueResolver(pair.Store), TimeProvider.System, NullLogger<FlowRuntime>.Instance, pair.Journal);
    private static FakeExecutableModuleCatalog Catalog(IDeliverModule deliver, Action? fetched = null) => new FakeExecutableModuleCatalog()
        .AddFetch(new DelegateFetchModule("fetch", (_, _, _, _) => { fetched?.Invoke(); return Task.FromResult(Batch()); }))
        .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
        .AddRender(DelegateModuleFactory.PassthroughRender("render"))
        .AddDeliver(deliver);

    public static IEnumerable<object[]> ConfirmationCases()
    {
        foreach (var provider in WorkspacePersistenceTests.Providers())
        {
            yield return [provider[0], DeliveryConfirmationStatus.Confirmed, 1, true];
            yield return [provider[0], DeliveryConfirmationStatus.NotDelivered, 2, true];
            yield return [provider[0], DeliveryConfirmationStatus.Unknown, 1, false];
        }
    }

    [Theory]
    [MemberData(nameof(ConfirmationCases))]
    public async Task InterruptedDelivery_VerifiesBeforeAnyReplay(string provider, DeliveryConfirmationStatus status, int expectedSends, bool completes)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var first = fixture.Journal();
        var module = new Destination { Confirmation = status };
        var flow = Pipeline();
        var catalog = Catalog(module);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, first).ExecuteAsync("orders", default));
        var id = Assert.Single(module.Sent);
        var restarted = fixture.Journal();
        await restarted.Journal.RecoverAsync(default);
        var retry = Assert.Single(restarted.Store.GetState().PendingRetries);
        Assert.False(await restarted.Journal.IsDeliveryHeldAsync(retry.ExecutionId, default));
        if (completes)
        {
            var result = await Runtime(flow, catalog, restarted).ResumeRetryAsync(retry, default);
            Assert.Equal(FlowExecutionOutcome.Completed, result!.Outcome);
            Assert.Empty(restarted.Store.GetState().PendingRetries);
        }
        else
        {
            await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, restarted).ResumeRetryAsync(retry, default));
            Assert.Single(restarted.Store.GetState().PendingRetries);
        }
        Assert.Equal(expectedSends, module.Sent.Count);
        Assert.All(module.Sent, sent => Assert.Equal(id, sent));
        Assert.Equal(id, Assert.Single(module.Checked));
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task ConfirmedQuery_RestoresResponseAndPersistsItForLaterRecovery(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var module = new Destination { Confirmation = DeliveryConfirmationStatus.Confirmed, Response = Batch() };
        var flow = Pipeline();
        var action = flow.Deliveries[0].Delivers[0];
        action.Deliver.Settings["expectsResponse"] = "true";
        var catalog = Catalog(module);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ExecuteAsync("orders", default));
        var restarted = fixture.Journal();
        await restarted.Journal.RecoverAsync(default);
        var retry = Assert.Single(restarted.Store.GetState().PendingRetries);
        var result = await Runtime(flow, catalog, restarted).ResumeRetryAsync(retry, default);
        Assert.Equal("one", Assert.Single(result!.ResponsePayloads!).GetText());
        var context = new FlowExecutionContext("orders", retry.ExecutionId, retry.ExecutionStartedAt);
        var input = new IntegrationBatch(retry.InputPayloads.Select(snapshot => snapshot.Restore()).ToArray());
        var intent = await restarted.Journal.PrepareDeliveryIntentAsync(context, retry.StageId, retry.PipelineSnapshot!.Deliveries[0].Delivers[0], input, default);
        Assert.Equal("Succeeded", intent.State);
        Assert.Equal("one", Assert.Single(context.CapturedResponses).GetText());
        Assert.Single(module.Sent);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task UnsupportedAndJsonStorage_AreRejectedBeforeFetch(string provider)
    {
        var fetched = 0;
        var module = new DelegateDeliverModule("confirmed", (_, _, _, _) => throw new Exception("must not deliver"));
        var flow = Pipeline();
        var catalog = Catalog(module, () => fetched++);
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ExecuteAsync("orders", default));
        var capable = Catalog(new Destination(), () => fetched++);
        var legacy = fixture.Legacy();
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), capable, new FlowOrchestrationStateStore(legacy),
            new FlowRetryStateStore(legacy), new ConfigValueResolver(legacy), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => runtime.ExecuteAsync("orders", default));
        Assert.Equal(0, fetched);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task FlowLink_ConfirmSurvivesClaimAndDeduplicates(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var module = new FlowLinkDeliverModule(pair.Journal);
        var step = new ModuleStepDefinition { Module = "flow-link-deliver", Settings = new() { ["channel"] = "orders" } };
        var context = new FlowExecutionContext("producer", "run", DateTimeOffset.UtcNow);
        var id = Guid.NewGuid();
        Assert.Equal(DeliveryConfirmationStatus.Confirmed, (await module.DeliverOnceAsync(id, context, Batch(), step, default)).Status);
        var consumer = new FlowExecutionContext("consumer", "consumer-run", DateTimeOffset.UtcNow);
        Assert.Single((await pair.Journal.ClaimAsync("flow-link:orders", consumer, default)).Payloads);
        var restarted = fixture.Journal();
        var restored = new FlowLinkDeliverModule(restarted.Journal);
        Assert.Equal(DeliveryConfirmationStatus.Confirmed, (await restored.ConfirmAsync(id, context, Batch(), step, default)).Status);
        await restored.DeliverOnceAsync(id, context, Batch(), step, default);
        using var db = fixture.Factory.CreateDbContext();
        Assert.Single(db.Messages);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task FlowLink_RecipientRetentionCannotEraseDedupWhileSenderAcknowledgementIsLost(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var clock = new Clock();
        var pair = fixture.Journal(clock: clock);
        var inbox = new LostInboxAcknowledgement(pair.Journal);
        var flow = Pipeline("flow-link-deliver");
        flow.Deliveries[0].Delivers[0].Deliver.Settings["channel"] = "retention";
        var catalog = Catalog(new FlowLinkDeliverModule(inbox));
        await Assert.ThrowsAsync<FlowDurabilityException>(() => Runtime(flow, catalog, pair).ExecuteAsync("orders", default));
        var retry = Assert.Single(pair.Store.GetState().PendingRetries);
        var consumer = new FlowExecutionContext("consumer", "consumer-run", clock.GetUtcNow());
        await pair.Journal.BeginAsync(new() { Id = "consumer" }, consumer, default);
        Assert.Single((await pair.Journal.ClaimAsync("flow-link:retention", consumer, default)).Payloads);
        await pair.Journal.CompleteAsync(consumer, "flow-link:retention", true, default);
        await pair.Journal.FinishAsync(consumer, "Completed", null, default);
        clock.Now = clock.Now.AddDays(31);
        Assert.Equal(1, await pair.Journal.PurgeAsync(default));
        using (var db = fixture.Factory.CreateDbContext())
        {
            var message = Assert.Single(db.Messages);
            Assert.Equal("Completed", message.State);
            Assert.Equal(Assert.Single(db.Deliveries).Id, message.Id);
        }
        var restarted = fixture.Journal(clock: clock);
        var resumedCatalog = Catalog(new FlowLinkDeliverModule(restarted.Journal));
        Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(flow, resumedCatalog, restarted).ResumeRetryAsync(retry, default))!.Outcome);
        Assert.Equal(1, inbox.Enqueues);
        Assert.Empty((await restarted.Journal.ClaimAsync("flow-link:retention", new("other", "other-run", clock.GetUtcNow()), default)).Payloads);
        using var inspected = fixture.Factory.CreateDbContext();
        Assert.Single(inspected.Messages);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task InboxCapacity_AllowsDuplicateEvenWhenFullAndRejectsChangedContent(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal(history: new FlowHistoryOptions { MaxPendingMessages = 1 });
        var id = Guid.NewGuid();
        await pair.Journal.EnqueueAsync("flow-link:test", null, id, Batch(), default);
        await pair.Journal.EnqueueAsync("flow-link:test", null, id, Batch(), default);
        await Assert.ThrowsAsync<AdmissionRejectedException>(() => pair.Journal.EnqueueAsync("flow-link:test", null, Guid.NewGuid(), Batch(), default));
        await Assert.ThrowsAsync<FlowDurabilityException>(() => pair.Journal.EnqueueAsync("http:test", "flow", id, Batch(), default));
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task ConfirmedIntentRejectsChangedInputAndPolicy(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var flow = Pipeline();
        var context = new FlowExecutionContext("orders", "run", DateTimeOffset.UtcNow);
        await pair.Journal.BeginAsync(flow, context, default);
        var action = flow.Deliveries[0].Delivers[0];
        var intent = await pair.Journal.PrepareDeliveryIntentAsync(context, "delivery:0:deliver:0", action, Batch(), default);
        Assert.NotEqual(Guid.Empty, intent.Id);
        var changed = new IntegrationBatch([new("body", BinaryData.FromString("different"), "text/plain", new Dictionary<string, string>())]);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => pair.Journal.PrepareDeliveryIntentAsync(context, "delivery:0:deliver:0", action, changed, default));
        await Assert.ThrowsAsync<FlowDurabilityException>(() => pair.Journal.PrepareDeliveryIntentAsync(context, "delivery:0:deliver:0", new() { Deliver = action.Deliver }, Batch(), default));
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task FlowLink_OrdinaryRecipientBodyExpiresWhileConfirmedReceiptRemains(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var clock = new Clock();
        var pair = fixture.Journal(clock: clock);
        var module = new FlowLinkDeliverModule(pair.Journal);
        var context = new FlowExecutionContext("producer", "producer-run", clock.GetUtcNow());
        var ordinary = new ModuleStepDefinition { Module = "flow-link-deliver", Settings = new() { ["channel"] = "ordinary" } };
        var confirmed = new ModuleStepDefinition { Module = "flow-link-deliver", Settings = new() { ["channel"] = "confirmed" } };
        var id = Guid.NewGuid();
        await module.DeliverAsync(context, Batch(), ordinary, default);
        await module.DeliverOnceAsync(id, context, Batch(), confirmed, default);
        foreach (var channel in new[] { "ordinary", "confirmed" })
        {
            var consumer = new FlowExecutionContext("consumer", channel + "-run", clock.GetUtcNow());
            await pair.Journal.BeginAsync(new() { Id = "consumer" }, consumer, default);
            Assert.Single((await pair.Journal.ClaimAsync("flow-link:" + channel, consumer, default)).Payloads);
            await pair.Journal.CompleteAsync(consumer, "flow-link:" + channel, true, default);
            await pair.Journal.FinishAsync(consumer, "Completed", null, default);
        }
        using (var before = fixture.Factory.CreateDbContext())
        {
            Assert.False(before.Messages.Single(row => row.Channel == "flow-link:ordinary").RetainForConfirmation);
            Assert.True(before.Messages.Single(row => row.Id == id).RetainForConfirmation);
        }
        clock.Now = clock.Now.AddDays(31);
        Assert.Equal(2, await pair.Journal.PurgeAsync(default));
        var restarted = fixture.Journal(clock: clock);
        var restored = new FlowLinkDeliverModule(restarted.Journal);
        Assert.Equal(DeliveryConfirmationStatus.Confirmed, (await restored.ConfirmAsync(id, context, Batch(), confirmed, default)).Status);
        await restored.DeliverOnceAsync(id, context, Batch(), confirmed, default);
        using var after = fixture.Factory.CreateDbContext();
        var receipt = Assert.Single(after.Messages);
        Assert.Equal(id, receipt.Id);
        Assert.True(receipt.RetainForConfirmation);
        Assert.Equal("Completed", receipt.State);
        Assert.Equal("flow-link:confirmed", receipt.Channel);
    }

    [Fact]
    public void OldDefinitionsDefaultToUnconfirmed()
    {
        var action = JsonSerializer.Deserialize<DeliveryActionDefinition>("{\"deliver\":{\"module\":\"x\"}}", JsonSerializerOptions.Web)!;
        Assert.False(action.RequireConfirmation);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class LostInboxAcknowledgement(IDurableInbox inner) : IDurableInbox
    {
        public int Enqueues { get; private set; }
        public Task EnqueueAsync(string channel, string? flowId, Guid messageId, IntegrationBatch batch, CancellationToken ct)
            => inner.EnqueueAsync(channel, flowId, messageId, batch, ct);
        public async Task EnqueueConfirmedAsync(string channel, Guid messageId, IntegrationBatch batch, CancellationToken ct)
        {
            Enqueues++;
            await inner.EnqueueConfirmedAsync(channel, messageId, batch, ct);
            throw new IOException("Inbox committed but acknowledgement was lost.");
        }
        public Task<bool> ContainsAsync(string channel, Guid id, IntegrationBatch batch, CancellationToken ct) => inner.ContainsAsync(channel, id, batch, ct);
        public Task<IntegrationBatch> ClaimAsync(string channel, FlowExecutionContext context, CancellationToken ct) => inner.ClaimAsync(channel, context, ct);
        public Task CompleteAsync(FlowExecutionContext context, string channel, bool succeeded, CancellationToken ct) => inner.CompleteAsync(context, channel, succeeded, ct);
    }

    private sealed class Destination : IConfirmedDeliveryModule
    {
        public ModuleDescriptor Descriptor { get; } = new("confirmed", "confirmed", ModuleKind.Deliver, "test");
        public bool IsConfirmationAvailable => true;
        public DeliveryConfirmationStatus Confirmation { get; init; }
        public IntegrationBatch? Response { get; init; }
        public List<Guid> Sent { get; } = [];
        public List<Guid> Checked { get; } = [];
        public Task DeliverAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken ct) => throw new InvalidOperationException();
        public Task<ConfirmedDeliveryResult> ConfirmAsync(Guid id, FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken ct)
        {
            Checked.Add(id);
            return Task.FromResult(new ConfirmedDeliveryResult(Confirmation, Response));
        }
        public Task<ConfirmedDeliveryResult> DeliverOnceAsync(Guid id, FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken ct)
        {
            Sent.Add(id);
            if (Sent.Count == 1) throw new IOException("Acknowledgement lost");
            return Task.FromResult(new ConfirmedDeliveryResult(DeliveryConfirmationStatus.Confirmed));
        }
    }
}
