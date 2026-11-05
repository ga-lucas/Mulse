using System.Text.Json;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;

namespace Service.Tests;

public sealed class NativeDurableControlFlowTests
{
    private static NativeFlowDefinition Flow(params NativeFlowNode[] nodes) => new() { Durable = true, Nodes = nodes.ToList() };
    private static NativeFlowNode Render(string module) => new() { Kind = NativeFlowNodeKind.Render, Module = module };
    private static NativeFlowNode Wait(int seconds = 10) => new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = seconds };
    private static NativeFlowNode Receive(string name = "next", int seconds = 0) => new()
    {
        Kind = NativeFlowNodeKind.Receive, MessageName = name, Channel = "input", DurationSeconds = seconds,
        CorrelationKeys = ["tenant"], Conditions = [Condition("kind", "accepted")]
    };
    private static DecisionConditionDefinition Condition(string key, string value) => new()
    { Source = DecisionValueSourceKind.Metadata, Path = key, Operator = DecisionComparisonOperator.Equals, Value = value };
    private static ModuleStepDefinition Step(NativeFlowDefinition flow) => new()
    { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(flow, JsonSerializerOptions.Web) } };
    private static IntegrationBatch Input(string id, string tenant = "T", string channel = "input", string kind = "accepted", string group = "A") => new([
        new(id + ".json", BinaryData.FromString("{\"value\":\"" + id + "\"}"), "application/json",
            new Dictionary<string, string> { ["messageId"] = id, ["group"] = group, ["tenant"] = tenant, ["channel"] = channel, ["kind"] = kind })]);
    private static FlowExecutionContext Context(Clock clock, string? key = null, string? execution = null)
    {
        var context = new FlowExecutionContext("native", execution ?? Guid.NewGuid().ToString("N"), clock.Now);
        if (key is not null) context.SetOrchestrationInstance("resume", key, "native:resume", true);
        return context;
    }
    private static Task<IntegrationBatch> Run(IFlowOrchestrationStateStore store, Clock clock, Invoker invoker,
        NativeFlowDefinition flow, FlowExecutionContext context, IntegrationBatch input, CancellationToken token = default)
        => new NativeControlFlowAugmentModule(invoker, invoker, store, clock, invoker).AugmentAsync(context, input, Step(flow), token);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Invoker : IAugmentModuleInvoker, IRenderModuleInvoker, IDeliverModuleInvoker
    {
        public List<string> Calls { get; } = [];
        public Func<string, FlowExecutionContext, IntegrationBatch, Task<IntegrationBatch>>? Action { get; set; }
        public Task<IntegrationBatch> RenderAsync(string module, FlowExecutionContext context, IntegrationBatch batch,
            IReadOnlyDictionary<string, string> settings, CancellationToken token)
        {
            Calls.Add(module + ":" + string.Join(",", batch.Payloads.Select(payload => payload.Name)));
            token.ThrowIfCancellationRequested();
            return Action?.Invoke(module, context, batch) ?? Task.FromResult(batch);
        }
        public Task<IntegrationBatch> AugmentAsync(string module, FlowExecutionContext context, IntegrationBatch batch,
            IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(module, context, batch, settings, token);
        public Task<IntegrationBatch> DeliverAsync(string module, FlowExecutionContext context, IntegrationBatch batch,
            IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(module, context, batch, settings, token);
    }
    private sealed class DiskStore(string path) : IFlowOrchestrationStateStore
    {
        public bool FailIntentBeforeCommit { get; set; }
        public bool FailIntentAfterCommit { get; set; }
        public bool FailOutcomeBeforeCommit { get; set; }
        public bool FailOutcomeAfterCommit { get; set; }
        public Task<FlowOrchestrationCheckpoint?> GetAsync(string flowId, string key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(File.Exists(path) ? JsonSerializer.Deserialize<FlowOrchestrationCheckpoint>(File.ReadAllText(path), JsonSerializerOptions.Web) : null);
        }
        public async Task UpsertAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken token)
        {
            var intent = checkpoint.Native?.State.EffectNodeId is not null;
            if (intent && FailIntentBeforeCommit) throw new IOException("Intent write failed before commit.");
            if (!intent && FailOutcomeBeforeCommit && (await GetAsync(checkpoint.FlowId, checkpoint.CorrelationKey, token))?.Native?.State.EffectNodeId is not null)
                throw new IOException("Effect outcome could not be committed.");
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(checkpoint, JsonSerializerOptions.Web), token);
            if (intent && FailIntentAfterCommit) throw new IOException("Lost intent commit acknowledgement.");
            if (!intent && FailOutcomeAfterCommit) throw new IOException("Lost outcome commit acknowledgement.");
        }
        public Task DeleteAsync(string flowId, string key, CancellationToken token) { File.Delete(path); return Task.CompletedTask; }
        public async Task<IReadOnlyList<FlowOrchestrationCheckpoint>> GetAllAsync(string flowId, CancellationToken token)
            => await GetAsync(flowId, "", token) is { } saved ? [saved] : [];
        public async Task<IReadOnlyList<FetchProcessingReceipt>> GetPendingSourceReceiptsAsync(string flowId, CancellationToken token)
            => (await GetAsync(flowId, "", token))?.SourceReceipts ?? [];
    }
    private static string DiskPath() => Path.Combine(Directory.GetCurrentDirectory(), "native-checkpoint-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task DiskRestartResumesNestedForeachChosenBranchesWithoutRepeatingUpstreamOrRender()
    {
        var path = DiskPath();
        try
        {
            var clock = new Clock(); var invoker = new Invoker();
            invoker.Action = (module, _, batch) => Task.FromResult(module == "expand" ? new IntegrationBatch([
                new("a", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string> { ["kind"] = "A" }),
                new("b", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string> { ["kind"] = "B" })]) : batch);
            var flow = Flow(Render("expand"), new() { Kind = NativeFlowNodeKind.ForEach, MaxIterations = 2, Children = [new()
            {
                Kind = NativeFlowNodeKind.Sequence, Children = [new()
                {
                    Kind = NativeFlowNodeKind.If, Conditions = [Condition("kind", "A")],
                    Children = [Render("branch-a"), Wait()], Otherwise = [Render("branch-b"), Wait()]
                }]
            }] });
            var first = Context(clock); await Run(new DiskStore(path), clock, invoker, flow, first, Input("one"));
            Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
            Assert.Equal(new[] { "expand:one.json", "branch-a:a" }, invoker.Calls);
            var saved = (await new DiskStore(path).GetAsync("native", "", default))!;
            Assert.Null(saved.Odx); Assert.NotNull(saved.Native); Assert.Equal(first.ExecutionId, saved.Native.LastExecutionId);
            Assert.Equal(clock.Now.AddSeconds(10), saved.Native.DeadlineUtc); Assert.False(saved.Native.RecoveryPending);
            clock.Now = clock.Now.AddSeconds(10);
            var second = Context(clock, first.OrchestrationCorrelationKey);
            await Run(new DiskStore(path), clock, invoker, flow, second, IntegrationBatch.Empty);
            Assert.Equal(FlowExecutionDisposition.Suspended, second.Disposition);
            Assert.Equal(new[] { "expand:one.json", "branch-a:a", "branch-b:b" }, invoker.Calls);
            clock.Now = clock.Now.AddSeconds(10);
            var third = Context(clock, first.OrchestrationCorrelationKey);
            var output = await Run(new DiskStore(path), clock, invoker, flow, third, IntegrationBatch.Empty);
            Assert.Equal(new[] { "a", "b" }, output.Payloads.Select(payload => payload.Name));
            Assert.NotNull(third.PendingCheckpointCompletion); Assert.True(File.Exists(path));
            Assert.Equal("Completed", (await new DiskStore(path).GetAsync("native", "", default))!.Native!.State.Status);
            var retry = Context(clock, first.OrchestrationCorrelationKey);
            Assert.Equal(2, (await Run(new DiskStore(path), clock, invoker, flow, retry, IntegrationBatch.Empty)).Count);
            Assert.Equal(3, invoker.Calls.Count);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NamedMessagesReceiptsAndRepliesPersistAcrossStoreRestart(bool disk)
    {
        var path = DiskPath();
        try
        {
            IFlowOrchestrationStateStore store = disk ? new DiskStore(path) : new InMemoryFlowOrchestrationStateStore();
            var clock = new Clock(); var invoker = new Invoker();
            var flow = Flow(Receive("original"), new() { Kind = NativeFlowNodeKind.SaveMessage, MessageName = "saved" }, Wait(),
                Receive("other"), new() { Kind = NativeFlowNodeKind.UseMessage, MessageName = "saved" },
                new() { Kind = NativeFlowNodeKind.Reply, MessageName = "other" },
                new() { Kind = NativeFlowNodeKind.Deliver, Module = "send", MessageName = "saved" });
            flow.OutputMessage = "other";
            var first = Context(clock);
            first.RegisterSourceReceipt(new("receipt-one", "fetch", new() { Module = "fetch" }, new()));
            await Run(store, clock, invoker, flow, first, Input("one"));
            var key = first.OrchestrationCorrelationKey!;
            clock.Now = clock.Now.AddSeconds(10);
            if (disk) store = new DiskStore(path);
            await Run(store, clock, invoker, flow, Context(clock, key), IntegrationBatch.Empty);
            var resumed = Context(clock);
            resumed.RegisterSourceReceipt(new("receipt-two", "fetch", new() { Module = "fetch" }, new()));
            var output = await Run(store, clock, invoker, flow, resumed, Input("two"));
            Assert.Equal("two.json", Assert.Single(output.Payloads).Name);
            Assert.Equal("send:one.json", Assert.Single(invoker.Calls));
            Assert.Equal(new[] { "receipt-one", "receipt-two" }, resumed.SourceReceipts.Select(receipt => receipt.Id).Order(StringComparer.Ordinal));
            Assert.Contains(resumed.CapturedResponses, payload => payload.Name == "two.json");
            var saved = (await store.GetAsync("native", key, default))!;
            Assert.Equal(new[] { "receipt-one", "receipt-two" }, saved.SourceReceipts.Select(receipt => receipt.Id));
            Assert.True(saved.Native!.RecoveryPending); Assert.Null(saved.Native.DeadlineUtc);
            Assert.Equal(new[] { "one", "two" }, saved.Native.State.AcceptedMessageIds);
            Assert.Equal("one.json", saved.Native.State.Messages["saved"].Single().Name);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("T2", "input", "accepted", "two")]
    [InlineData("T", "other", "accepted", "two")]
    [InlineData("T", "input", "rejected", "two")]
    [InlineData("T", "input", "accepted", "one")]
    public async Task MismatchedOrDuplicateArrivalsNeverChangeOrAcknowledgeSavedReceive(string tenant, string channel, string kind, string id)
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var flow = Flow(Receive("first"), Render("upstream"), Receive());
        var first = Context(clock); await Run(store, clock, invoker, flow, first, Input("one"));
        var before = JsonSerializer.Serialize(store.Checkpoints.Single(), JsonSerializerOptions.Web);
        var bad = Context(clock);
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => Run(store, clock, invoker, flow, bad, Input(id, tenant, channel, kind)));
        Assert.Null(bad.PendingCheckpointCompletion);
        Assert.Empty(bad.SourceReceipts);
        Assert.Equal(before, JsonSerializer.Serialize(store.Checkpoints.Single(), JsonSerializerOptions.Web));
        Assert.Single(invoker.Calls);
        var accepted = Context(clock);
        Assert.Single((await Run(store, clock, invoker, flow, accepted, Input("two"))).Payloads);
        Assert.NotNull(accepted.PendingCheckpointCompletion);
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => Run(store, clock, invoker, flow, Context(clock), Input("two")));
    }

    [Fact]
    public async Task InitialReceiveMismatchDoesNotCreateCheckpointOrInvokeUnreachableEffects()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => Run(store, clock, invoker,
            Flow(Receive(), Render("later")), Context(clock), Input("one", channel: "wrong")));
        Assert.Empty(store.Checkpoints); Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task ReceiveTimeoutPinsDeadlineRejectsLateInputAndFaultsOnlyOnTimerResume()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var flow = Flow(Receive("first"), Receive(seconds: 5), Render("never"));
        var first = Context(clock); await Run(store, clock, invoker, flow, first, Input("one"));
        var deadline = clock.Now.AddSeconds(5);
        Assert.Equal(deadline, store.Checkpoints.Single().Native!.DeadlineUtc);
        clock.Now = deadline;
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => Run(store, clock, invoker, flow, Context(clock), Input("two")));
        Assert.Equal("Running", store.Checkpoints.Single().Native!.State.Status);
        await Assert.ThrowsAsync<TimeoutException>(() => Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty));
        var saved = store.Checkpoints.Single().Native!;
        Assert.True(saved.RecoveryPending); Assert.Equal("Faulted", saved.State.Status); Assert.Null(saved.DeadlineUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty));
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task WhileCursorRetainsCompletedIterationAndUpdatedWorkingBatchAcrossWaits()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        invoker.Action = (_, _, batch) => Task.FromResult(new IntegrationBatch(batch.Payloads.Select(payload => payload with
        { Metadata = payload.Metadata.Concat(new[] { new KeyValuePair<string, string>("done", "yes") }).ToDictionary(pair => pair.Key, pair => pair.Value) }).ToArray()));
        var flow = Flow(new NativeFlowNode { Kind = NativeFlowNodeKind.While, MaxIterations = 1, Conditions = [Condition("kind", "accepted")], Children =
            [Render("update"), Wait(), new() { Kind = NativeFlowNodeKind.If, Conditions = [Condition("done", "yes")], Children = [Receive()] }] });
        var first = Context(clock); await Run(store, clock, invoker, flow, first, Input("one"));
        clock.Now = clock.Now.AddSeconds(10);
        await Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty);
        var secondInput = Input("two");
        var condition = flow.Nodes[0].Conditions[0];
        flow.Nodes[0].Conditions = [Condition("done", "yes")];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, flow, Context(clock), secondInput));
        flow.Nodes[0].Conditions = [condition];
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, flow, Context(clock), secondInput));
        Assert.Single(invoker.Calls);
        Assert.True(store.Checkpoints.Single().Native!.RecoveryPending);
    }

    [Fact]
    public async Task WhileCompletesFromUpdatedOutputWithoutRepeatingAnIterationAfterRestart()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        invoker.Action = (_, _, batch) => Task.FromResult(new IntegrationBatch(batch.Payloads.Select(payload => payload with
        {
            Metadata = payload.Metadata.ToDictionary(pair => pair.Key, pair => pair.Key == "kind" && invoker.Calls.Count == 2 ? "done" : pair.Value)
        }).ToArray()));
        var flow = Flow(new NativeFlowNode { Kind = NativeFlowNodeKind.While, MaxIterations = 2, Conditions = [Condition("kind", "accepted")], Children = [Render("update"), Wait()] });
        var first = Context(clock); await Run(store, clock, invoker, flow, first, Input("one"));
        clock.Now = clock.Now.AddSeconds(10);
        var second = Context(clock, first.OrchestrationCorrelationKey);
        await Run(store, clock, invoker, flow, second, IntegrationBatch.Empty);
        Assert.Equal(FlowExecutionDisposition.Suspended, second.Disposition);
        Assert.Equal(2, invoker.Calls.Count);
        clock.Now = clock.Now.AddSeconds(10);
        var third = Context(clock, first.OrchestrationCorrelationKey);
        var output = await Run(store, clock, invoker, flow, third, IntegrationBatch.Empty);
        Assert.Equal("done", output.Payloads.Single().Metadata["kind"]);
        Assert.NotNull(third.PendingCheckpointCompletion); Assert.Equal(2, invoker.Calls.Count);
    }

    [Fact]
    public async Task CompletedDefinitionRemainsPinnedUntilExplicitDownstreamCompletionDeletion()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var flow = Flow(Render("upstream"), Wait());
        var first = Context(clock); await Run(store, clock, invoker, flow, first, Input("one"));
        var changed = Flow(Render("different"), Wait());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, changed,
            Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty));
        clock.Now = clock.Now.AddSeconds(10);
        var done = Context(clock, first.OrchestrationCorrelationKey);
        await Run(store, clock, invoker, flow, done, IntegrationBatch.Empty);
        Assert.Single(store.Checkpoints); Assert.NotNull(done.PendingCheckpointCompletion);
        flow.OutputMessage = "not-initialized";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty));
        Assert.Single(invoker.Calls);
        var completion = done.PendingCheckpointCompletion!.Value;
        await store.DeleteAsync(completion.FlowId, completion.CorrelationKey, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EffectIntentIsPersistedBeforeInvocationAndFailureFreezesWithoutBlindReplay(bool disk)
    {
        var path = DiskPath();
        try
        {
            IFlowOrchestrationStateStore store = disk ? new DiskStore(path) : new InMemoryFlowOrchestrationStateStore();
            var clock = new Clock(); var invoker = new Invoker(); var node = Render("effect"); var flow = Flow(node, Render("later"));
            invoker.Action = async (_, context, _) =>
            {
                var checkpoint = (await store.GetAsync(context.FlowId, context.OrchestrationCorrelationKey!, default))!;
                Assert.Equal(node.Id, checkpoint.Native!.State.EffectNodeId);
                Assert.True(checkpoint.Native.RecoveryPending);
                throw new IOException("Remote outcome unknown.");
            };
            await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(store, clock, invoker, flow, Context(clock), Input("one")));
            if (disk) store = new DiskStore(path);
            var saved = (await store.GetAsync("native", NativeDurableExecutor.CorrelationKey("A"), default))!;
            Assert.Equal("Faulted", saved.Native!.State.Status);
            Assert.Equal(node.Id, saved.Native.State.EffectNodeId);
            await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(store, clock, invoker, flow,
                Context(clock, saved.CorrelationKey), IntegrationBatch.Empty));
            Assert.Single(invoker.Calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedIntentAcknowledgementNeverInvokesEffectAndCommittedIntentFreezesRestart(bool committed)
    {
        var path = DiskPath();
        try
        {
            var store = new DiskStore(path) { FailIntentBeforeCommit = !committed, FailIntentAfterCommit = committed };
            var clock = new Clock(); var invoker = new Invoker(); var flow = Flow(Render("effect"));
            await Assert.ThrowsAsync<NativePersistenceException>(() => Run(store, clock, invoker, flow, Context(clock), Input("one")));
            Assert.Empty(invoker.Calls);
            if (committed)
                await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(new DiskStore(path), clock, invoker, flow,
                    Context(clock, NativeDurableExecutor.CorrelationKey("A")), IntegrationBatch.Empty));
            else Assert.False(File.Exists(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task EffectReturnedButOutcomePersistenceFailedRemainsFrozenAfterDiskRestart()
    {
        var path = DiskPath();
        try
        {
            var store = new DiskStore(path) { FailOutcomeBeforeCommit = true };
            var clock = new Clock(); var invoker = new Invoker(); var flow = Flow(Render("effect"), Render("later"));
            await Assert.ThrowsAsync<NativePersistenceException>(() => Run(store, clock, invoker, flow, Context(clock), Input("one")));
            Assert.Equal("effect:one.json", Assert.Single(invoker.Calls));
            var saved = (await new DiskStore(path).GetAsync("native", "", default))!;
            Assert.Equal(flow.Nodes[0].Id, saved.Native!.State.EffectNodeId);
            await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(new DiskStore(path), clock, invoker, flow,
                Context(clock, saved.CorrelationKey), IntegrationBatch.Empty));
            Assert.Single(invoker.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MissingNamedOutputFaultsRatherThanDeletingCheckpointOrRunningDownstream()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var flow = Flow(Wait(0)); flow.OutputMessage = "missing";
        var context = Context(clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, flow, context, Input("one")));
        Assert.Null(context.PendingCheckpointCompletion); Assert.Equal("Faulted", store.Checkpoints.Single().Native!.State.Status);
    }

    [Fact]
    public async Task ExactCorrelationIdentitiesDoNotCollapseCaseAndTimerResumeCannotCreateAnInstance()
    {
        Assert.NotEqual(NativeDurableExecutor.CorrelationKey("a"), NativeDurableExecutor.CorrelationKey("A"));
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, Flow(Wait()),
            Context(clock, NativeDurableExecutor.CorrelationKey("missing")), IntegrationBatch.Empty));
        var first = Context(clock); var flow = Flow(Receive("first"), Receive());
        await Run(store, clock, invoker, flow, first, Input("one"));
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => Run(store, clock, invoker, flow,
            Context(clock, first.OrchestrationCorrelationKey), Input("two", group: "a")));
        Assert.Single(store.Checkpoints);
    }

    [Fact]
    public void NativeSchemaRejectsInactiveFieldsTransactionsAndUnreachableBlockers()
    {
        static void Invalid(NativeFlowDefinition flow) => Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(flow, JsonSerializerOptions.Web)));
        Invalid(new() { Nodes = [Wait()] });
        Invalid(Flow(new NativeFlowNode { Kind = NativeFlowNodeKind.Sequence, DurationSeconds = 1 }));
        Invalid(Flow(new NativeFlowNode { Kind = NativeFlowNodeKind.Receive, MessageName = "msg", Channel = "input", CorrelationKeys = ["key", "key"] }));
        Invalid(Flow(new NativeFlowNode { Kind = NativeFlowNodeKind.SaveMessage, MessageName = "msg", Children = [Render("hidden")] }));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read("{\"version\":1,\"durable\":true,\"nodes\":[{\"id\":\"tx\",\"kind\":\"Transaction\"}]}"));
        var blocked = Flow(Render("first"), new() { Kind = NativeFlowNodeKind.Blocked, BlockReason = "Transactions are unsupported", Children = [Wait()] });
        Assert.Throws<NotSupportedException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(blocked, JsonSerializerOptions.Web)));
        Assert.Equal(3, NativeFlowDefinition.Read(JsonSerializer.Serialize(blocked, JsonSerializerOptions.Web), allowBlocked: true).AllNodes().Count());
    }

    [Fact]
    public async Task CorruptCursorCannotSkipEffectsOrSilentlyReset()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker(); var flow = Flow(Wait(), Render("never"));
        var first = Context(clock); await Run(store, clock, invoker, flow, first, Input("one"));
        store.Checkpoints.Single().Native!.State.Stack[^1].NodeId = "unknown";
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, invoker, flow,
            Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty));
        Assert.Empty(invoker.Calls); Assert.Single(store.Checkpoints);
    }

    [Theory]
    [InlineData(NativeFlowNodeKind.Render)]
    [InlineData(NativeFlowNodeKind.Augment)]
    [InlineData(NativeFlowNodeKind.Deliver)]
    public async Task VerifiedCommittedEffectRecoversNestedCursorWithoutInvocationReplay(NativeFlowNodeKind kind)
    {
        var path = DiskPath();
        try
        {
            var clock = new Clock(); var invoker = new Invoker(); var effect = new NativeFlowNode { Kind = kind, Module = "effect" };
            var flow = Flow(new NativeFlowNode { Kind = NativeFlowNodeKind.ForEach, Children = [
                new() { Kind = NativeFlowNodeKind.SaveMessage, MessageName = "original" }, effect, Wait(0)] });
            invoker.Action = (_, _, _) => throw new IOException("Outcome unknown");
            var first = Context(clock);
            first.RegisterSourceReceipt(new("held", "fetch", new() { Module = "fetch" }, new()));
            await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(new DiskStore(path), clock, invoker, flow, first, Input("one")));
            var recovery = new NativeDurableRecovery(new DiskStore(path), clock);
            var pending = await recovery.GetPendingEffectAsync("native", first.OrchestrationCorrelationKey!, flow, default);
            Assert.Equal(new NativePendingEffect(effect.Id, kind, "effect"), pending);
            Assert.Equal(new[] { "EffectId", "Kind", "Module", "NodeId", "RequiresOutput" }, typeof(NativePendingEffect).GetProperties().Select(property => property.Name).Order(StringComparer.Ordinal));
            var before = (await new DiskStore(path).GetAsync("native", "", default))!;
            Assert.Equal(pending, NativeDurableRecovery.Inspect(before)); Assert.True(pending!.RequiresOutput);
            var verifiedPayload = Input("verified").Payloads.Single();
            var verified = new IntegrationBatch([verifiedPayload with { Metadata = new Dictionary<string, string>(verifiedPayload.Metadata, StringComparer.Ordinal) { ["Group"] = "separate" } }]);
            await NativeDurableRecovery.ReconcileCommittedAsync(before, flow, effect.Id, "Destination audit record 123", verified, new DiskStore(path), clock, default);
            var committed = (await new DiskStore(path).GetAsync("native", "", default))!;
            Assert.Equal(before.Native!.State.Stack.Count - 1, committed.Native!.State.Stack.Count);
            Assert.Equal(before.Native.State.Stack[^2].Position, committed.Native.State.Stack[^1].Position);
            Assert.Null(committed.Native.State.EffectNodeId); Assert.False(committed.Native.RecoveryPending);
            Assert.Equal(clock.Now, committed.Native.DeadlineUtc); Assert.Equal(first.ExecutionId, committed.Native.LastExecutionId);
            Assert.Equal("one.json", committed.Native.State.Messages["original"].Single().Name);
            Assert.Equal("held", committed.SourceReceipts.Single().Id);
            var audit = Assert.Single(committed.Native.State.CommittedEffects);
            Assert.Equal(effect.Id, audit.NodeId); Assert.Equal("Destination audit record 123", audit.OperatorEvidence);
            Assert.Equal(before.Native.State.Operations, audit.Operation);
            Assert.Null(await recovery.GetPendingEffectAsync("native", first.OrchestrationCorrelationKey!, flow, default));
            var resumed = Context(clock, first.OrchestrationCorrelationKey);
            var output = await Run(new DiskStore(path), clock, invoker, flow, resumed, IntegrationBatch.Empty);
            Assert.Equal(kind == NativeFlowNodeKind.Deliver ? "one.json" : "verified.json", output.Payloads.Single().Name);
            if (kind == NativeFlowNodeKind.Deliver) Assert.Equal("verified.json", resumed.CapturedResponses.Single().Name);
            var recovered = kind == NativeFlowNodeKind.Deliver ? resumed.CapturedResponses.Single() : output.Payloads.Single();
            Assert.Equal("A", recovered.Metadata["group"]); Assert.Equal("separate", recovered.Metadata["Group"]);
            Assert.Single(invoker.Calls); Assert.NotNull(resumed.PendingCheckpointCompletion);
            await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.ConfirmCommittedAsync("native", first.OrchestrationCorrelationKey!, flow,
                effect.Id, "Already reconciled", IntegrationBatch.Empty, default));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RecoveryRejectsStaleIdentityDefinitionMissingOutputAndBlankEvidenceWithoutMutation()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var effect = Render("effect"); var flow = Flow(effect);
        invoker.Action = (_, _, _) => throw new IOException("Unknown effect");
        var first = Context(clock);
        await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(store, clock, invoker, flow, first, Input("one")));
        var recovery = new NativeDurableRecovery(store, clock); var key = first.OrchestrationCorrelationKey!;
        var before = JsonSerializer.Serialize(store.Checkpoints.Single(), JsonSerializerOptions.Web);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.ConfirmCommittedAsync("native", key, flow, "stale", "Verified", Input("verified"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.ConfirmCommittedAsync("native", key, flow, effect.Id, " ", Input("verified"), default));
        await Assert.ThrowsAsync<ArgumentException>(() => recovery.ConfirmCommittedAsync("native", key, flow, effect.Id, "Verified", null, default));
        var changed = Flow(new NativeFlowNode { Id = effect.Id, Kind = effect.Kind, Module = "changed" });
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.ConfirmCommittedAsync("native", key, changed, effect.Id, "Verified", Input("verified"), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.GetPendingEffectAsync("native", key, changed, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.ConfirmCommittedAsync("NATIVE", key, flow, effect.Id, "Verified", Input("verified"), default));
        Assert.Equal(before, JsonSerializer.Serialize(store.Checkpoints.Single(), JsonSerializerOptions.Web));
        await recovery.ConfirmCommittedAsync("native", key, flow, effect.Id, "Verified empty transform output", IntegrationBatch.Empty, default);
        Assert.Empty(store.Checkpoints.Single().Payloads);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.ConfirmCommittedAsync("native", key, flow, effect.Id, "Stale", Input("verified"), default));
        Assert.Single(invoker.Calls);
    }

    [Fact]
    public async Task RecoveryRejectsReceiveFaultAndWaitWithoutInflightEffect()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock(); var invoker = new Invoker();
        var receive = Receive(seconds: 1); var flow = Flow(Receive("first"), receive);
        var first = Context(clock); await Run(store, clock, invoker, flow, first, Input("one"));
        var recovery = new NativeDurableRecovery(store, clock);
        Assert.Null(await recovery.GetPendingEffectAsync("native", first.OrchestrationCorrelationKey!, flow, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.ConfirmCommittedAsync("native", first.OrchestrationCorrelationKey!, flow,
            receive.Id, "No external commit", IntegrationBatch.Empty, default));
        clock.Now = clock.Now.AddSeconds(1);
        await Assert.ThrowsAsync<TimeoutException>(() => Run(store, clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => recovery.ConfirmCommittedAsync("native", first.OrchestrationCorrelationKey!, flow,
            receive.Id, "No external commit", IntegrationBatch.Empty, default));
        Assert.Empty(invoker.Calls); Assert.Equal("Faulted", store.Checkpoints.Single().Native!.State.Status);
    }

    [Fact]
    public async Task VerifiedCommitPersistenceFailurePreservesIntentUntilExplicitSuccessfulConfirmation()
    {
        var path = DiskPath();
        try
        {
            var clock = new Clock(); var invoker = new Invoker(); var effect = Render("effect"); var flow = Flow(effect);
            invoker.Action = (_, _, _) => throw new IOException("Unknown effect");
            var first = Context(clock);
            await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(new DiskStore(path), clock, invoker, flow, first, Input("one")));
            var recovery = new NativeDurableRecovery(new DiskStore(path) { FailOutcomeBeforeCommit = true }, clock);
            await Assert.ThrowsAsync<NativePersistenceException>(() => recovery.ConfirmCommittedAsync("native", first.OrchestrationCorrelationKey!, flow,
                effect.Id, "Verified at destination", Input("verified"), default));
            var saved = (await new DiskStore(path).GetAsync("native", "", default))!;
            Assert.Equal(effect.Id, saved.Native!.State.EffectNodeId); Assert.Empty(saved.Native.State.CommittedEffects);
            await new NativeDurableRecovery(new DiskStore(path), clock).ConfirmCommittedAsync("native", first.OrchestrationCorrelationKey!, flow,
                effect.Id, "Verified at destination", Input("verified"), default);
            var result = await Run(new DiskStore(path), clock, invoker, flow, Context(clock, first.OrchestrationCorrelationKey), IntegrationBatch.Empty);
            Assert.Equal("verified.json", result.Payloads.Single().Name); Assert.Single(invoker.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task LostVerifiedCommitAcknowledgementNeverDuplicatesResponseOrReplaysEffect()
    {
        var path = DiskPath();
        try
        {
            var clock = new Clock(); var invoker = new Invoker();
            var effect = new NativeFlowNode { Kind = NativeFlowNodeKind.Deliver, Module = "effect" }; var flow = Flow(effect);
            invoker.Action = (_, _, _) => throw new IOException("Unknown delivery");
            var first = Context(clock);
            await Assert.ThrowsAsync<NativeAmbiguousEffectException>(() => Run(new DiskStore(path), clock, invoker, flow, first, Input("one")));
            var inspected = (await new DiskStore(path).GetAsync("native", "", default))!;
            await Assert.ThrowsAsync<NativePersistenceException>(() => NativeDurableRecovery.ReconcileCommittedAsync(inspected, flow,
                effect.Id, "Verified response", Input("response"), new DiskStore(path) { FailOutcomeAfterCommit = true }, clock, default));
            await Assert.ThrowsAsync<InvalidOperationException>(() => NativeDurableRecovery.ReconcileCommittedAsync(inspected, flow,
                effect.Id, "Stale confirmation", Input("response"), new DiskStore(path), clock, default));
            var current = (await new DiskStore(path).GetAsync("native", "", default))!;
            Assert.Null(NativeDurableRecovery.Inspect(current)); Assert.Single(current.Native!.State.Responses);
            Assert.Single(current.Native.State.CommittedEffects);
            var context = Context(clock, first.OrchestrationCorrelationKey);
            await Run(new DiskStore(path), clock, invoker, flow, context, IntegrationBatch.Empty);
            Assert.Single(context.CapturedResponses); Assert.Single(invoker.Calls); Assert.NotNull(context.PendingCheckpointCompletion);
        }
        finally { File.Delete(path); }
    }
}
