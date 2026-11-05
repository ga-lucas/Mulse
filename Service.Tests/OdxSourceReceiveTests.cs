using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.Orchestration;

namespace Service.Tests;

public sealed class OdxSourceReceiveTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string type, string name, params object[] contents) => new(Ns + "Element", new XAttribute("Type", type), new XAttribute("OID", name), P("Name", name), contents);
    private static XElement Filter(string name, string key, string value, string grouping = "AND", string operation = "Equals") => N("DNFPredicate", name,
        P("LHS", key), P("RHS", value), P("Grouping", grouping), P("Operator", operation));
    private static XElement CorrelationType() => N("CorrelationType", "BatchType", N("PropertyRef", "Batch", P("Ref", "Example.Batch")),
        N("PropertyRef", "Destination", P("Ref", "Example.Destination")), N("PropertyRef", "Group", P("Ref", "Example.Group")));
    private static OdxProgram Program(bool externalType = false, string followTarget = "next") => Read(N("Module", "Example.Process",
        externalType ? Array.Empty<object>() : [CorrelationType()],
        N("ServiceDeclaration", "Service",
            N("CorrelationDeclaration", "batch", P("Type", "Example.Process.BatchType"), P("ParamDirection", "In"),
                N("StatementRef", "start-ref", P("Ref", "first"), P("Initializes", "True")),
                N("StatementRef", "follow-ref", P("Ref", followTarget), P("Initializes", "False"))),
            N("ServiceBody", "Body",
                N("Receive", "first", P("PortName", "input"), P("MessageName", "initial"), P("Activate", "True"), P("OperationName", "Submit"), P("OperationMessageName", "Request"),
                    Filter("confirmed", "Example.Confirmed", "true"), Filter("kind", "Example.Kind", "\"Trailer\"")),
                N("Receive", "next", P("PortName", "input"), P("MessageName", "following"), P("Activate", "False"), P("OperationName", "Submit"), P("OperationMessageName", "Request"))))));
    private static OdxProgram Read(XElement root) => OdxProgramReader.Read("<?xml version='1.0'?>" + new XElement(Ns + "MetaModel", root));
    private static OdxNode Service(OdxProgram program) => program.Roots[0].Children.Single(node => node.ShapeType == "ServiceDeclaration");
    private static OdxExecutionFrame Frame(OdxProgram program) => new(program, new("source-receives", "run", DateTimeOffset.UtcNow), IntegrationBatch.Empty, new());
    private static OdxIncomingMessage Input(string id, string group = "A", string confirmed = "true", string kind = "Trailer", string port = "input") => new(id, port,
        new([new(id, BinaryData.FromString("<Input/>"), "application/xml", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Example.Batch"] = "batch", ["Example.Destination"] = "destination", ["Example.Group"] = group,
            ["Example.Confirmed"] = confirmed, ["Example.Kind"] = kind
        })]), [new(id, "source", new() { Module = "source" }, new())]) { OperationName = "Submit", OperationMessageName = "Request" };

    [Fact]
    public async Task SourceStatementRefsPinThreePropertiesAndResumeFollowingReceiveAfterDiskRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-source-receives-" + Guid.NewGuid().ToString("N"));
        try
        {
            var program = Program(); var bindings = new OdxDurableBindings(); OdxReceiveBindings.Bind(program, Service(program), bindings);
            FlowOrchestrationStateStore Store() => new(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }),
                new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
            Task<OdxDurableResult> Run(OdxIncomingMessage input, OdxExecutionFrame frame) => new OdxDurableExecutor(Store(), TimeProvider.System)
                .ExecuteAsync(program, Service(program), bindings, frame, "instance", "source-v1", input, default);
            Assert.Equal(OdxDurableOutcome.Waiting, (await Run(Input("first"), Frame(program))).Outcome);
            var held = Assert.Single(await Store().GetAllAsync("source-receives", default)); Assert.Single(held.SourceReceipts);
            Assert.Equal(OdxDurableOutcome.Unmatched, (await Run(Input("wrong-client", group: "a"), Frame(program))).Outcome);
            Assert.Equal(OdxDurableOutcome.Unmatched, (await Run(Input("wrong-operation") with { OperationName = "Other" }, Frame(program))).Outcome);
            Assert.Equal(OdxDurableOutcome.Unmatched, (await Run(Input("wrong-response") with { OperationMessageName = "Response" }, Frame(program))).Outcome);
            Assert.Single((await Store().GetAsync("source-receives", "instance", default))!.SourceReceipts);
            var restored = Frame(program);
            // Activation predicates belong to the initial receive, not the following subscription.
            Assert.Equal(OdxDurableOutcome.Completed, (await Run(Input("next", confirmed: "false", kind: "Other"), restored)).Outcome);
            Assert.Equal(new[] { "first", "next" }, restored.Context.SourceReceipts.Select(receipt => receipt.Id));
            Assert.Equal("first", restored.Messages["initial"].Payloads.Single().Name);
            Assert.Equal("next", restored.Messages["following"].Payloads.Single().Name);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("true", "Trailer", true)]
    [InlineData("True", "Trailer", true)]
    [InlineData("1", "Trailer", true)]
    [InlineData("false", "Trailer", false)]
    [InlineData("invalid", "Trailer", false)]
    [InlineData("true", "trailer", false)]
    public void ActualSourceAndEqualsFilterShapeUsesTypedBooleanAndOrdinalStringValues(string confirmed, string kind, bool expected)
    {
        var receive = Service(Program()).Children.Single(node => node.ShapeType == "ServiceBody").Children[0];
        Assert.Equal(expected, OdxReceiveBindings.CompileFilter(receive)(Input("event", confirmed: confirmed, kind: kind).Batch.Payloads[0]));
    }

    [Fact]
    public void MissingOrCaseChangedPromotedKeysDoNotMatch_AndNoUnsupportedPredicateIsDropped()
    {
        var receive = Service(Program()).Children.Single(node => node.ShapeType == "ServiceBody").Children[0]; var filter = OdxReceiveBindings.CompileFilter(receive);
        var payload = Input("event").Batch.Payloads[0]; var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase);
        metadata.Remove("Example.Confirmed"); metadata["example.confirmed"] = "true";
        Assert.False(filter(payload with { Metadata = metadata }));
        foreach (var predicate in new[] { Filter("unsupported", "Example.Kind", "\"Trailer\"", "OR"), Filter("unsupported", "Example.Kind", "\"Trailer\"", operation: "NotEquals"),
            Filter("unsupported", "Example.Kind", "Dangerous.Call()"), Filter("unsupported", "Example.Kind", "null") })
        {
            var model = Read(N("Module", "M", N("Receive", "r", predicate)));
            Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.CompileFilter(model.Roots[0].Children[0]));
        }
    }

    [Fact]
    public void ExistsRequiresTheExactPromotedKeyAndDoesNotRequireANonemptyValue()
    {
        var model = Read(N("Module", "M", N("Receive", "r", N("DNFPredicate", "exists", P("LHS", "BTS.ReceivePortName"), P("Grouping", "AND"), P("Operator", "Exists")))));
        var filter = OdxReceiveBindings.CompileFilter(model.Roots[0].Children[0]);
        var payload = Input("event").Batch.Payloads[0]; Assert.False(filter(payload));
        Assert.True(filter(payload with { Metadata = new Dictionary<string, string> { ["BTS.ReceivePortName"] = "" } }));
        Assert.False(filter(payload with { Metadata = new Dictionary<string, string> { ["bts.receiveportname"] = "port" } }));
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public void SourcePredicateAndCorrelationPropertyCountsEnforceTheirExactLimit(int count, bool reject)
    {
        var predicates = Enumerable.Range(0, count).Select(index => Filter("p" + index, "Example.P" + index, "true")).ToArray();
        var receive = Read(N("Module", "M", N("Receive", "input", predicates))).Roots[0].Children[0];
        var external = Program(externalType: true);
        var definition = Read(N("Module", "Example.Process", N("CorrelationType", "BatchType",
            Enumerable.Range(0, count).Select(index => N("PropertyRef", "p" + index, P("Ref", "Example.P" + index))).ToArray())));
        if (reject)
        {
            Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.CompileFilter(receive));
            Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.Bind(external, Service(external), new(), [definition]));
        }
        else
        {
            var payload = Input("event").Batch.Payloads[0] with { Metadata = Enumerable.Range(0, count).ToDictionary(index => "Example.P" + index, _ => "true") };
            Assert.True(OdxReceiveBindings.CompileFilter(receive)(payload));
            Assert.Equal(count, OdxReceiveBindings.Bind(external, Service(external), new(), [definition]).Count);
        }
    }

    [Theory]
    [InlineData("False", "False")]
    [InlineData("True", "True")]
    [InlineData("True", "")]
    public void UnsupportedActivationTopologyIsNotSilentlyApproximated(string first, string next)
    {
        var program = Read(N("Module", "M", N("ServiceDeclaration", "Service", N("ServiceBody", "Body",
            N("Receive", "first", P("PortName", "input"), P("MessageName", "first"), P("Activate", first)),
            N("Receive", "next", P("PortName", "input"), P("MessageName", "next"), next.Length > 0 ? new[] { P("Activate", next) } : [])))));
        Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.Bind(program, Service(program), new()));
    }

    [Fact]
    public void ExistsCannotIgnoreAnUnexpectedRightHandSide()
    {
        var receive = Read(N("Module", "M", N("Receive", "input", Filter("exists", "BTS.ReceivePortName", "\"ignored\"", operation: "Exists")))).Roots[0].Children[0];
        Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.CompileFilter(receive));
    }

    [Fact]
    public void ExactExternalCorrelationTypeResolutionAndStatementReferencesAreRequiredBeforeAnyExecution()
    {
        var external = Program(externalType: true); var bindings = new OdxDurableBindings();
        Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.Bind(external, Service(external), bindings));
        var definitions = Read(N("Module", "Example.Process", CorrelationType()));
        OdxReceiveBindings.Bind(external, Service(external), new(), [definitions]);
        Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.Bind(external, Service(external), new(), [definitions, definitions]));
        var missing = Program(followTarget: "does-not-exist");
        Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.Bind(missing, Service(missing), new()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeNodeRoutesIndependentSourceCorrelationTuplesWithoutAManualInstanceField(bool externalType)
    {
        var program = Program(externalType); var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var module = new OdxProgramAugmentModule(checkpoints, TimeProvider.System, new NoRender());
        var step = new ModuleStepDefinition { Module = OdxProgramAugmentModule.ModuleId, Settings = new()
        {
            ["programJson"] = JsonSerializer.Serialize(program, JsonSerializerOptions.Web), ["correlationMetadataKey"] = "",
            ["messageIdMetadataKey"] = "messageId", ["inputPort"] = "input", ["inputOperation"] = "Submit", ["inputOperationMessage"] = "Request", ["outputMessage"] = "following",
            ["referencedProgramsJson"] = JsonSerializer.Serialize(externalType ? new[] { Read(N("Module", "Example.Process", CorrelationType())) } : [], JsonSerializerOptions.Web)
        } };
        async Task<FlowExecutionContext> Run(string id, string group, string destination = "destination")
        {
            var message = Input(id, group); var payload = message.Batch.Payloads[0];
            var metadata = new Dictionary<string, string>(payload.Metadata) { ["messageId"] = id, ["Example.Destination"] = destination };
            var context = new FlowExecutionContext("source-receives", id, DateTimeOffset.UtcNow);
            foreach (var receipt in message.SourceReceipts) context.RegisterSourceReceipt(receipt);
            var output = await module.AugmentAsync(context, new([payload with { Metadata = metadata }]), step, default);
            if (context.Disposition == FlowExecutionDisposition.Continue) Assert.Equal(id, output.Payloads.Single().Name);
            return context;
        }
        Assert.Equal(FlowExecutionDisposition.Suspended, (await Run("first-A", "A")).Disposition);
        Assert.Equal(FlowExecutionDisposition.Suspended, (await Run("first-a", "a")).Disposition);
        Assert.Equal(2, checkpoints.Checkpoints.Count);
        Assert.Equal(2, checkpoints.Checkpoints.Select(checkpoint => checkpoint.CorrelationKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var resumed = await Run("next-A", "A");
        Assert.Equal(FlowExecutionDisposition.Continue, resumed.Disposition);
        Assert.Equal(new[] { "next-A", "first-A" }.Order(), resumed.SourceReceipts.Select(receipt => receipt.Id).Order());
        Assert.Equal(FlowExecutionDisposition.Continue, (await Run("next-a", "a")).Disposition);
        Assert.Equal(FlowExecutionDisposition.Suspended, (await Run("tuple-one", "B|C", "A")).Disposition);
        Assert.Equal(FlowExecutionDisposition.Suspended, (await Run("tuple-two", "C", "A|B")).Disposition);
        Assert.Equal(4, checkpoints.Checkpoints.Select(checkpoint => checkpoint.CorrelationKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static XElement SendCorrelation(string name, string initializer, string? following = null) => N("CorrelationDeclaration", name,
        P("Type", "Example.Process.BatchType"), P("ParamDirection", "In"),
        N("StatementRef", name + "-init", P("Ref", initializer), P("Initializes", "True")),
        following is null ? Array.Empty<object>() : [N("StatementRef", name + "-next", P("Ref", following), P("Initializes", "False"))]);
    private static IEnumerable<OdxNode> Nodes(IEnumerable<OdxNode> roots) => roots.SelectMany(node => new[] { node }.Concat(Nodes(node.Children)));

    [Fact]
    public async Task OutboundInitializedScopeResumesOnDiskAndReinitializesOnTheNextLoopEntry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-scoped-correlations-" + Guid.NewGuid().ToString("N"));
        try
        {
            var program = Read(N("Module", "Example.Process", CorrelationType(), N("ServiceDeclaration", "Service",
                N("ServiceBody", "Body", N("Receive", "input", P("PortName", "input"), P("MessageName", "request")),
                    N("While", "loop", P("Expression", "count < 2"), N("Scope", "scope",
                        N("Send", "outbound", P("PortName", "out"), P("MessageName", "request")),
                        SendCorrelation("batch", "outbound", "reply"),
                        N("Receive", "reply", P("PortName", "input"), P("MessageName", "reply")),
                        N("VariableAssignment", "increment", P("Expression", "count++;")),
                        N("MessageAssignment", "change-group")))))));
            FlowOrchestrationStateStore Store() => new(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }),
                new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
            var sent = new List<string>();
            OdxDurableBindings Bind()
            {
                var bindings = new OdxDurableBindings(); OdxReceiveBindings.Bind(program, Service(program), bindings);
                var automatic = OdxAutomaticBindings.Compile(program);
                foreach (var pair in automatic.Bindings.Activities) { bindings.Execution.Activities[pair.Key] = pair.Value; bindings.FrameLocalActivities.Add(pair.Key); }
                foreach (var pair in automatic.Bindings.Conditions) bindings.Execution.Conditions[pair.Key] = pair.Value;
                var send = Nodes(program.Roots).Single(node => node.Name == "outbound");
                bindings.Execution.Activities[send.Id] = OdxMessageBindings.BindSend(send, new Dictionary<string, OdxPortSender>
                {
                    ["out"] = (_, batch, _) => { sent.Add(batch.Payloads[0].Metadata["Example.Group"]); return Task.FromResult(IntegrationBatch.Empty); }
                });
                var change = Nodes(program.Roots).Single(node => node.Name == "change-group");
                bindings.Execution.Activities[change.Id] = (_, frame, _) => { frame.Messages["request"] = Input("request-B", "B").Batch; return Task.CompletedTask; };
                bindings.FrameLocalActivities.Add(change.Id);
                return bindings;
            }
            Task<OdxDurableResult> Run(OdxIncomingMessage input, OdxExecutionFrame frame) => new OdxDurableExecutor(Store(), TimeProvider.System)
                .ExecuteAsync(program, Service(program), Bind(), frame, "instance", "scoped-v1", input, default);
            var first = Frame(program); first.Variables["count"] = 0;
            Assert.Equal(OdxDurableOutcome.Waiting, (await Run(Input("initial"), first)).Outcome);
            Assert.Equal(new[] { "A" }, sent);
            Assert.Equal(OdxDurableOutcome.Unmatched, (await Run(Input("wrong", "B"), Frame(program))).Outcome);
            Assert.Equal(OdxDurableOutcome.Waiting, (await Run(Input("reply-A"), Frame(program))).Outcome);
            Assert.Equal(new[] { "A", "B" }, sent);
            var final = Frame(program);
            Assert.Equal(OdxDurableOutcome.Completed, (await Run(Input("reply-B", "B"), final)).Outcome);
            Assert.Equal(2, final.Variables["count"]!.GetValue<int>());
            Assert.DoesNotContain(final.Variables.Keys, key => key.StartsWith("$odx.sourceCorrelation.", StringComparison.Ordinal));
            Assert.Equal(new[] { "initial", "reply-A", "reply-B" }, final.Context.SourceReceipts.Select(receipt => receipt.Id));
            Assert.Equal(2, sent.Count);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("case")]
    [InlineData("uninitialized")]
    [InlineData("mismatch")]
    [InlineData("multiple")]
    public async Task InvalidOutboundCorrelationFailsBeforeTransportOrEffectIntent(string invalid)
    {
        var program = Read(N("Module", "Example.Process", CorrelationType(), N("ServiceDeclaration", "Service",
            SendCorrelation("batch", "outbound", invalid == "uninitialized" ? "before" : "after"),
            N("ServiceBody", "Body", N("Receive", "input", P("PortName", "input"), P("MessageName", "request")),
                N("Send", "before", P("PortName", "out"), P("MessageName", "request")),
                N("Send", "outbound", P("PortName", "out"), P("MessageName", "request")),
                N("Send", "after", P("PortName", "out"), P("MessageName", "request"))))));
        var bindings = new OdxDurableBindings(); OdxReceiveBindings.Bind(program, Service(program), bindings);
        var calls = 0;
        foreach (var send in Nodes(program.Roots).Where(node => node.ShapeType == "Send"))
            bindings.Execution.Activities[send.Id] = (_, frame, _) =>
            {
                calls++;
                if (invalid == "mismatch") frame.Messages["request"] = Input("other", "B").Batch;
                return Task.CompletedTask;
            };
        var message = Input("initial"); var payload = message.Batch.Payloads[0];
        var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal);
        if (invalid == "missing") metadata.Remove("Example.Group");
        if (invalid == "case") { metadata.Remove("Example.Group"); metadata["example.group"] = "A"; }
        // Invalid metadata is introduced by a deterministic construct before any correlated send.
        var before = Nodes(program.Roots).Single(node => node.Name == "before");
        if (invalid != "uninitialized")
        {
            bindings.Execution.Activities[before.Id] = (_, frame, _) =>
            {
                frame.Messages["request"] = invalid == "multiple" ? new([payload, payload]) : new([payload with { Metadata = metadata }]);
                return Task.CompletedTask;
            };
            bindings.FrameLocalActivities.Add(before.Id);
        }
        var store = new InMemoryFlowOrchestrationStateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OdxDurableExecutor(store, TimeProvider.System)
            .ExecuteAsync(program, Service(program), bindings, Frame(program), "instance", "invalid-v1", message, default));
        Assert.Equal(invalid == "mismatch" ? 1 : 0, calls);
        var state = System.Text.Json.Nodes.JsonNode.Parse(Assert.Single(store.Checkpoints).StateValues["odx.continuation.v1"])!;
        Assert.Null(state["effectNodeId"]); Assert.Equal("Faulted", state["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task PreviousInterpreterCursorOrderingCannotResumeUnderTheNewCorrelationLifetimeSemantics()
    {
        var program = Program(); var bindings = new OdxDurableBindings(); OdxReceiveBindings.Bind(program, Service(program), bindings);
        var store = new InMemoryFlowOrchestrationStateStore();
        Task<OdxDurableResult> Run(OdxIncomingMessage input) => new OdxDurableExecutor(store, TimeProvider.System)
            .ExecuteAsync(program, Service(program), bindings, Frame(program), "instance", "v1", input, default);
        Assert.Equal(OdxDurableOutcome.Waiting, (await Run(Input("initial"))).Outcome);
        var checkpoint = Assert.Single(store.Checkpoints); var json = System.Text.Json.Nodes.JsonNode.Parse(checkpoint.StateValues["odx.continuation.v1"])!;
        var legacyHash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(program, JsonSerializerOptions.Web) + "\nv1"));
        json["definitionHash"] = Convert.ToHexString(legacyHash); checkpoint.StateValues["odx.continuation.v1"] = json.ToJsonString();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Run(Input("following")));
        Assert.Contains("pinned runtime", error.Message); Assert.Single(checkpoint.SourceReceipts);
    }

    [Fact]
    public async Task ExceptionUnwindRemovesInnerScopeCorrelationBeforeTheOuterCatch()
    {
        var program = Read(N("Module", "Example.Process", CorrelationType(), N("ServiceDeclaration", "Service",
            N("ServiceBody", "Body", N("Receive", "input", P("PortName", "input"), P("MessageName", "request")),
                N("Scope", "scope", SendCorrelation("batch", "outbound"), N("Send", "outbound", P("PortName", "out"), P("MessageName", "request")),
                    N("VariableAssignment", "fault"))),
            N("Catch", "catch", P("ExceptionType", "System.InvalidOperationException"), N("VariableAssignment", "inspect")))));
        var bindings = new OdxDurableBindings(); OdxReceiveBindings.Bind(program, Service(program), bindings);
        var inspected = false;
        foreach (var node in Nodes(program.Roots).Where(node => node.Name is "outbound" or "fault" or "inspect"))
        {
            bindings.Execution.Activities[node.Id] = (_, frame, _) =>
            {
                if (node.Name == "fault") throw new InvalidOperationException("Synthetic deterministic failure");
                if (node.Name == "inspect")
                {
                    Assert.DoesNotContain(frame.Variables.Keys, key => key.StartsWith("$odx.sourceCorrelation.", StringComparison.Ordinal));
                    inspected = true;
                }
                return Task.CompletedTask;
            };
            if (node.Name != "outbound") bindings.FrameLocalActivities.Add(node.Id);
        }
        var result = await new OdxDurableExecutor(new InMemoryFlowOrchestrationStateStore(), TimeProvider.System)
            .ExecuteAsync(program, Service(program), bindings, Frame(program), "instance", "unwind-v1", Input("initial"), default);
        Assert.Equal(OdxDurableOutcome.Completed, result.Outcome); Assert.True(inspected);
    }

    [Fact]
    public void ScopedCorrelationCannotReferenceAStatementOutsideItsLifetime()
    {
        var program = Read(N("Module", "Example.Process", CorrelationType(), N("ServiceDeclaration", "Service", N("ServiceBody", "Body",
            N("Scope", "scope", SendCorrelation("batch", "outside")), N("Send", "outside", P("PortName", "out"), P("MessageName", "request"))))));
        Assert.Throws<NotSupportedException>(() => OdxReceiveBindings.Bind(program, Service(program), new()));
    }

    private sealed class NoRender : IRenderModuleInvoker
    {
        public Task<IntegrationBatch> RenderAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
            => throw new InvalidOperationException("Receive-only source must not render.");
    }

    [Fact]
    public async Task MissingInitializerPropertyCannotInitializeOrAcceptTheCorrelation()
    {
        var program = Program(); var bindings = new OdxDurableBindings(); OdxReceiveBindings.Bind(program, Service(program), bindings);
        var input = Input("first"); var payload = input.Batch.Payloads.Single();
        var metadata = new Dictionary<string, string>(payload.Metadata); metadata.Remove("Example.Destination");
        input = input with { Batch = new([payload with { Metadata = metadata }]) };
        var store = new InMemoryFlowOrchestrationStateStore();
        var result = await new OdxDurableExecutor(store, TimeProvider.System).ExecuteAsync(program, Service(program), bindings, Frame(program), "instance", "v1", input, default);
        Assert.Equal(OdxDurableOutcome.Unmatched, result.Outcome); Assert.Empty(store.Checkpoints);
    }
}
