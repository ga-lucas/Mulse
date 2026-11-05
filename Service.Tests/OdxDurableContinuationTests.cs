using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mulse.Modules.Orchestration;

namespace Service.Tests;

public sealed class OdxDurableContinuationTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement Prop(string name, string value) => new(Ns + "Property", new XAttribute("Name", name), new XAttribute("Value", value));
    private static XElement Node(string type, string name, params object[] contents) => new(Ns + "Element", new XAttribute("Type", type), Prop("Name", name), contents);
    private static OdxProgram Program(params XElement[] body) => OdxProgramReader.Read("<?xml version='1.0'?>" + new XElement(Ns + "MetaModel",
        Node("ServiceDeclaration", "Service", Node("ServiceBody", "Body", body))));
    private static IEnumerable<OdxNode> Nodes(OdxProgram program) => Flatten(program.Roots);
    private static IEnumerable<OdxNode> Flatten(IEnumerable<OdxNode> nodes) => nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
    private static OdxNode Find(OdxProgram program, string name) => Nodes(program).Single(node => node.Name == name);
    private static OdxExecutionFrame Frame(OdxProgram program) => new(program, new("odx", Guid.NewGuid().ToString(), DateTimeOffset.UtcNow), IntegrationBatch.Empty, new());
    private static OdxIncomingMessage Message(string id, string group = "A", string port = "input") => new(id, port,
        new([new(id + ".xml", BinaryData.FromString("<Input/>"), "application/xml", new Dictionary<string, string> { ["group"] = group })]),
        [new(id, "fetch", new() { Module = "fetch" }, new())]);
    private static OdxDurableBindings Bind(OdxProgram program)
    {
        var automatic = OdxAutomaticBindings.Compile(program); var bindings = new OdxDurableBindings();
        foreach (var pair in automatic.Bindings.Activities) { bindings.Execution.Activities.Add(pair.Key, pair.Value); bindings.FrameLocalActivities.Add(pair.Key); }
        foreach (var pair in automatic.Bindings.Conditions) bindings.Execution.Conditions.Add(pair.Key, pair.Value);
        foreach (var receive in Nodes(program).Where(node => node.ShapeType == "Receive"))
            bindings.Receives.Add(receive.Id, (node, _) => new("input", node.Name, new Dictionary<string, string> { ["group"] = "A" }));
        return bindings;
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class DiskStore(string path) : IFlowOrchestrationStateStore
    {
        public int Writes { get; private set; }
        public int? FailAcknowledgement { get; set; }
        public Task<FlowOrchestrationCheckpoint?> GetAsync(string flowId, string correlationKey, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            return Task.FromResult(File.Exists(path) ? JsonSerializer.Deserialize<FlowOrchestrationCheckpoint>(File.ReadAllText(path), JsonSerializerOptions.Web) : null);
        }
        public async Task UpsertAsync(FlowOrchestrationCheckpoint checkpoint, CancellationToken token)
        {
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(checkpoint, JsonSerializerOptions.Web), token);
            if (++Writes == FailAcknowledgement) throw new IOException("Simulated lost commit acknowledgement");
        }
        public Task DeleteAsync(string flowId, string correlationKey, CancellationToken token) { File.Delete(path); return Task.CompletedTask; }
        public async Task<IReadOnlyList<FlowOrchestrationCheckpoint>> GetAllAsync(string flowId, CancellationToken token)
            => await GetAsync(flowId, "A", token) is { } saved ? [saved] : [];
        public async Task<IReadOnlyList<FetchProcessingReceipt>> GetPendingSourceReceiptsAsync(string flowId, CancellationToken token)
            => (await GetAsync(flowId, "A", token))?.SourceReceipts ?? [];
    }
    private static Task<OdxDurableResult> Run(IFlowOrchestrationStateStore store, Clock clock, OdxProgram program, OdxDurableBindings bindings,
        OdxExecutionFrame frame, OdxIncomingMessage? message = null, string version = "v1")
        => new OdxDurableExecutor(store, clock).ExecuteAsync(program, program.Roots[0], bindings, frame, "A", version, message, default);

    [Fact]
    public async Task NestedLoopsResumeTheNextStatementAfterDiskRestart_WithoutReinitializingOrReexecutingEffects()
    {
        var path = Path.GetTempFileName(); File.Delete(path);
        try
        {
            var program = Program(Node("VariableAssignment", "Initialize", Prop("Expression", "count = 0;")),
                Node("While", "Loop", Prop("Expression", "count < 2"), Node("Receive", "Next"),
                    Node("VariableAssignment", "Increment", Prop("Expression", "count++;")), Node("Send", "Effect")),
                Node("MessageAssignment", "Output"));
            var bindings = Bind(program); var effects = 0;
            bindings.Execution.Activities[Find(program, "Effect").Id] = (_, _, _) => { effects++; return Task.CompletedTask; };
            bindings.Execution.Activities[Find(program, "Output").Id] = (_, frame, _) => { frame.Batch = frame.Messages["Next"]; return Task.CompletedTask; };
            bindings.FrameLocalActivities.Add(Find(program, "Output").Id);
            var clock = new Clock(); var frame = Frame(program); frame.Variables["count"] = 999;
            Assert.Equal(OdxDurableOutcome.Waiting, (await Run(new DiskStore(path), clock, program, bindings, frame, Message("one"))).Outcome);
            Assert.Equal(1, effects); Assert.Equal(1, frame.Variables["count"]!.GetValue<int>());
            var resumed = Frame(program);
            Assert.Equal(OdxDurableOutcome.Completed, (await Run(new DiskStore(path), clock, program, bindings, resumed, Message("two"))).Outcome);
            Assert.Equal(2, effects); Assert.Equal(2, resumed.Variables["count"]!.GetValue<int>());
            Assert.Equal("two.xml", Assert.Single(resumed.Batch.Payloads).Name);
            Assert.Equal(new[] { "one", "two" }, resumed.Context.SourceReceipts.Select(receipt => receipt.Id));
            Assert.NotNull(resumed.Context.PendingCheckpointCompletion);
            var duplicate = Frame(program);
            Assert.Equal(OdxDurableOutcome.Completed, (await Run(new DiskStore(path), clock, program, bindings, duplicate, Message("two"))).Outcome);
            Assert.Equal(2, effects);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ListenPinsDeadlineAndCorrelationAcrossRestart_AndDoesNotConsumeAnotherClient()
    {
        var path = Path.GetTempFileName(); File.Delete(path);
        try
        {
            var program = Program(Node("Listen", "Listen", Node("ListenBranch", "Received", Node("Receive", "Input"),
                    Node("VariableAssignment", "SetReceived", Prop("Expression", "result = \"received\";"))),
                Node("ListenBranch", "TimedOut", Node("Delay", "Deadline"),
                    Node("VariableAssignment", "SetTimeout", Prop("Expression", "result = \"timeout\";")))));
            var bindings = Bind(program); var clock = new Clock(); var deadlineCalls = 0;
            bindings.Deadlines[Find(program, "Deadline").Id] = (_, _) => { deadlineCalls++; return clock.Now.AddMinutes(5); };
            var first = Frame(program); first.Variables["result"] = "pending";
            var waiting = await Run(new DiskStore(path), clock, program, bindings, first);
            Assert.Equal(OdxDurableOutcome.Waiting, waiting.Outcome); Assert.Equal(clock.Now.AddMinutes(5), waiting.NextDeadlineUtc);
            var unmatched = Frame(program);
            Assert.Equal(OdxDurableOutcome.Unmatched, (await Run(new DiskStore(path), clock, program, bindings, unmatched, Message("wrong", "B"))).Outcome);
            Assert.Empty(unmatched.Context.SourceReceipts); Assert.Equal(1, deadlineCalls);
            clock.Now = clock.Now.AddMinutes(6);
            Assert.Equal(OdxDurableOutcome.Unmatched, (await Run(new DiskStore(path), clock, program, bindings, Frame(program), Message("late"))).Outcome);
            var resumed = Frame(program);
            Assert.Equal(OdxDurableOutcome.Completed, (await Run(new DiskStore(path), clock, program, bindings, resumed)).Outcome);
            Assert.Equal("timeout", resumed.Variables["result"]!.GetValue<string>()); Assert.Equal(1, deadlineCalls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ListenSelectsReceiveBranchBeforeDeadline_AndSkipsItsTimerSibling()
    {
        var program = Program(Node("Listen", "Listen", Node("ListenBranch", "InputBranch", Node("Receive", "Input"), Node("Send", "Right")),
            Node("ListenBranch", "TimeoutBranch", Node("Delay", "Deadline"), Node("Send", "Wrong"))));
        var bindings = Bind(program); var clock = new Clock(); var visits = new List<string>();
        bindings.Deadlines[Find(program, "Deadline").Id] = (_, _) => clock.Now.AddMinutes(5);
        foreach (var node in Nodes(program).Where(node => node.ShapeType == "Send")) bindings.Execution.Activities[node.Id] = (node, _, _) => { visits.Add(node.Name); return Task.CompletedTask; };
        var store = new InMemoryFlowOrchestrationStateStore();
        Assert.Equal(OdxDurableOutcome.Completed, (await Run(store, clock, program, bindings, Frame(program), Message("one"))).Outcome);
        Assert.Equal(new[] { "Right" }, visits);
    }

    [Fact]
    public async Task CatchContinuationPreservesFaultAndScopeAcrossSuspension()
    {
        var path = Path.GetTempFileName(); File.Delete(path);
        try
        {
            var program = Program(Node("Scope", "Scope", Node("MessageAssignment", "Throw"),
                Node("Catch", "Catch", Prop("ExceptionType", "System.InvalidOperationException"), Node("Receive", "Recovery"), Node("MessageAssignment", "ReadFault"))),
                Node("MessageAssignment", "After"));
            var bindings = Bind(program); var throws = 0;
            bindings.Execution.Activities[Find(program, "Throw").Id] = (_, _, _) => { throws++; throw new InvalidOperationException("synthetic fault"); };
            bindings.FrameLocalActivities.Add(Find(program, "Throw").Id);
            bindings.Execution.Activities[Find(program, "ReadFault").Id] = (_, frame, _) => { Assert.Equal("synthetic fault", frame.CurrentException!.Message); return Task.CompletedTask; };
            bindings.FrameLocalActivities.Add(Find(program, "ReadFault").Id);
            bindings.Execution.Activities[Find(program, "After").Id] = (_, frame, _) => { Assert.Null(frame.CurrentException); return Task.CompletedTask; };
            bindings.FrameLocalActivities.Add(Find(program, "After").Id);
            var clock = new Clock();
            Assert.Equal(OdxDurableOutcome.Waiting, (await Run(new DiskStore(path), clock, program, bindings, Frame(program))).Outcome);
            Assert.Equal(OdxDurableOutcome.Completed, (await Run(new DiskStore(path), clock, program, bindings, Frame(program), Message("recovery"))).Outcome);
            Assert.Equal(1, throws);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AmbiguousExternalEffectIsNeverAutomaticallyReplayedOrCaughtBySourceHandlers()
    {
        var program = Program(Node("Scope", "Scope", Node("Send", "External"),
            Node("Catch", "Catch", Prop("ExceptionType", "System.Exception"), Node("Send", "MustNotRun"))));
        var bindings = Bind(program); var calls = 0;
        bindings.Execution.Activities[Find(program, "External").Id] = (_, _, _) => { calls++; throw new IOException("destination acknowledgement lost"); };
        bindings.Execution.Activities[Find(program, "MustNotRun").Id] = (_, _, _) => throw new Exception("Source catch must not replay effects");
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock();
        await Assert.ThrowsAsync<OdxAmbiguousEffectException>(() => Run(store, clock, program, bindings, Frame(program)));
        await Assert.ThrowsAsync<OdxAmbiguousEffectException>(() => Run(store, clock, program, bindings, Frame(program)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LostIntentWriteAcknowledgementBlocksExternalCallAndSubsequentReplay()
    {
        var path = Path.GetTempFileName(); File.Delete(path);
        try
        {
            var program = Program(Node("Send", "External")); var bindings = Bind(program); var calls = 0;
            bindings.Execution.Activities[Find(program, "External").Id] = (_, _, _) => { calls++; return Task.CompletedTask; };
            var store = new DiskStore(path) { FailAcknowledgement = 1 }; var clock = new Clock();
            await Assert.ThrowsAsync<OdxPersistenceException>(() => Run(store, clock, program, bindings, Frame(program)));
            Assert.Equal(0, calls);
            await Assert.ThrowsAsync<OdxAmbiguousEffectException>(() => Run(new DiskStore(path), clock, program, bindings, Frame(program)));
            Assert.Equal(0, calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RepliesAreRestoredAfterRestartWithoutRepeatingTheirCaptureActivity()
    {
        var path = Path.GetTempFileName(); File.Delete(path);
        try
        {
            var program = Program(Node("MessageAssignment", "CaptureReply"), Node("Receive", "FollowUp"));
            var bindings = Bind(program); var replies = 0;
            bindings.Execution.Activities[Find(program, "CaptureReply").Id] = (_, frame, _) =>
            {
                replies++; frame.Context.CaptureResponse(Message("reply").Batch); return Task.CompletedTask;
            };
            bindings.FrameLocalActivities.Add(Find(program, "CaptureReply").Id);
            var clock = new Clock();
            Assert.Equal(OdxDurableOutcome.Waiting, (await Run(new DiskStore(path), clock, program, bindings, Frame(program))).Outcome);
            var resumed = Frame(program);
            Assert.Equal(OdxDurableOutcome.Completed, (await Run(new DiskStore(path), clock, program, bindings, resumed, Message("next"))).Outcome);
            Assert.Single(resumed.Context.CapturedResponses); Assert.Equal(1, replies);
            var completed = Frame(program);
            await Run(new DiskStore(path), clock, program, bindings, completed, Message("next"));
            Assert.Single(completed.Context.CapturedResponses); Assert.Equal(1, replies);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SuccessfulEffectWithLostCompletionAcknowledgementIsNotRepeated()
    {
        var path = Path.GetTempFileName(); File.Delete(path);
        try
        {
            var program = Program(Node("Send", "External")); var bindings = Bind(program); var calls = 0;
            bindings.Execution.Activities[Find(program, "External").Id] = (_, _, _) => { calls++; return Task.CompletedTask; };
            var clock = new Clock(); var store = new DiskStore(path) { FailAcknowledgement = 2 };
            await Assert.ThrowsAsync<OdxPersistenceException>(() => Run(store, clock, program, bindings, Frame(program)));
            Assert.Equal(1, calls);
            var saved = await new DiskStore(path).GetAsync("odx", "A", default);
            Assert.False(saved!.Odx!.RecoveryPending); Assert.Equal(clock.Now, saved.Odx.DeadlineUtc);
            Assert.Equal(OdxDurableOutcome.Completed, (await Run(new DiskStore(path), clock, program, bindings, Frame(program))).Outcome);
            Assert.Equal(1, calls);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("position")]
    [InlineData("receive-as-timer")]
    [InlineData("null-stack")]
    [InlineData("null-required")]
    [InlineData("duplicate-required")]
    [InlineData("timer-operation")]
    public async Task CorruptPersistedCursorsAndSubscriptionsCannotSkipReceivesOrExecuteEffects(string corruption)
    {
        var program = Program(Node(corruption == "timer-operation" ? "Delay" : "Receive", "Input"), Node("Send", "External")); var bindings = Bind(program); var calls = 0;
        bindings.Deadlines[Find(program, "Input").Id] = (_, _) => new Clock().Now.AddMinutes(1);
        bindings.Execution.Activities[Find(program, "External").Id] = (_, _, _) => { calls++; return Task.CompletedTask; };
        var store = new InMemoryFlowOrchestrationStateStore(); var clock = new Clock();
        await Run(store, clock, program, bindings, Frame(program));
        var saved = Assert.Single(store.Checkpoints); var value = Assert.Single(saved.StateValues);
        var data = JsonNode.Parse(value.Value)!;
        switch (corruption)
        {
            case "position": data["stack"]![0]!["position"] = int.MaxValue; break;
            case "receive-as-timer": data["waits"]![0]!["port"] = null; data["waits"]![0]!["deadlineUtc"] = "2020-01-01T00:00:00Z"; break;
            case "null-stack": data["stack"] = null; break;
            case "null-required": data["waits"]![0]!["requiredMetadata"] = null; break;
            case "duplicate-required": data["waits"]![0]!["requiredMetadata"] = new JsonArray("group", "group"); break;
            case "timer-operation": data["waits"]![0]!["operationName"] = "InvalidTimerOperation"; break;
        }
        saved.StateValues[value.Key] = data.ToJsonString();
        await store.UpsertAsync(saved, default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, program, bindings, Frame(program)));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("null-required")]
    [InlineData("null-correlation")]
    [InlineData("duplicate-required")]
    [InlineData("blank-operation")]
    [InlineData("blank-operation-message")]
    public async Task InvalidSdkSubscriptionsFailExplicitlyRatherThanWithANullReference(string invalid)
    {
        var program = Program(Node("Receive", "Input")); var bindings = Bind(program);
        var subscription = new OdxReceiveSubscription("input", "Input", new Dictionary<string, string>());
        subscription = invalid switch
        {
            "null-required" => subscription with { RequiredMetadata = null! },
            "null-correlation" => subscription with { Correlation = null! },
            "duplicate-required" => subscription with { RequiredMetadata = ["group", "group"] },
            "blank-operation" => subscription with { OperationName = " " },
            "blank-operation-message" => subscription with { OperationMessageName = "" },
            _ => throw new ArgumentException("Unknown invalid subscription case.")
        };
        bindings.Receives[Find(program, "Input").Id] = (_, _) => subscription;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new InMemoryFlowOrchestrationStateStore(), new Clock(), program, bindings, Frame(program)));
        Assert.Contains("invalid subscription", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(10000, false)]
    [InlineData(10001, true)]
    public async Task PayloadSnapshotCountAcceptsItsExactLimitAndRejectsOverflow(int count, bool reject)
    {
        var program = Program(Node("Receive", "Input")); var store = new InMemoryFlowOrchestrationStateStore();
        var frame = Frame(program);
        var payload = new IntegrationPayload("small", BinaryData.FromString(""), "application/octet-stream", new Dictionary<string, string>());
        frame.Messages["prepared"] = new(Enumerable.Repeat(payload, count).ToArray());
        if (reject)
        {
            await Assert.ThrowsAsync<OdxPersistenceException>(() => Run(store, new Clock(), program, Bind(program), frame));
            Assert.Empty(store.Checkpoints);
        }
        else
        {
            Assert.Equal(OdxDurableOutcome.Waiting, (await Run(store, new Clock(), program, Bind(program), frame)).Outcome);
            Assert.Single(store.Checkpoints);
        }
    }

    [Theory]
    [InlineData(12000000, false)]
    [InlineData(12000001, true)]
    public async Task PayloadBytesAcceptTheirExactLimitAndRejectOverflow(int bytes, bool reject)
    {
        var program = Program(Node("Receive", "Input")); var store = new InMemoryFlowOrchestrationStateStore();
        var frame = Frame(program);
        frame.Batch = new([new("prepared", BinaryData.FromBytes(new byte[bytes]), "application/octet-stream", new Dictionary<string, string>())]);
        if (reject)
        {
            await Assert.ThrowsAsync<OdxPersistenceException>(() => Run(store, new Clock(), program, Bind(program), frame));
            Assert.Empty(store.Checkpoints);
        }
        else
        {
            Assert.Equal(OdxDurableOutcome.Waiting, (await Run(store, new Clock(), program, Bind(program), frame)).Outcome);
            Assert.Single(store.Checkpoints);
        }
    }

    [Fact]
    public async Task DefinitionChangesAndTransactionsAndMissingUntakenBindingsFailBeforeEffects()
    {
        var program = Program(Node("Receive", "Input")); var bindings = Bind(program); var clock = new Clock();
        var store = new InMemoryFlowOrchestrationStateStore();
        await Run(store, clock, program, bindings, Frame(program));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(store, clock, program, bindings, Frame(program), version: "changed"));
        var transactional = Program(Node("Scope", "Atomic", Node("AtomicTransaction", "Marker"), Node("Send", "Effect")));
        var txBindings = Bind(transactional); var calls = 0;
        txBindings.Execution.Activities[Find(transactional, "Effect").Id] = (_, _, _) => { calls++; return Task.CompletedTask; };
        await Assert.ThrowsAsync<NotSupportedException>(() => Run(new InMemoryFlowOrchestrationStateStore(), clock, transactional, txBindings, Frame(transactional)));
        Assert.Equal(0, calls);
        var unsupported = Program(Node("Listen", "Listen", Node("ListenBranch", "Branch", Node("Receive", "Input"),
            Node("Decision", "Decision", Node("DecisionBranch", "If", Prop("Expression", "false"), Node("Send", "Unbound"))))));
        await Assert.ThrowsAsync<NotSupportedException>(() => Run(new InMemoryFlowOrchestrationStateStore(), clock, unsupported, Bind(unsupported), Frame(unsupported), Message("one")));
    }
}
