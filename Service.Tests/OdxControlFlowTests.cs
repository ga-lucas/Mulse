using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules.Orchestration;

namespace Service.Tests;

public sealed class OdxControlFlowTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement Node(string type, string name, params object[] contents) => new(Ns + "Element",
        new XAttribute("Type", type), new XAttribute("OID", "oid-" + name), new XAttribute("ParentLink", "link-" + type),
        Prop("Name", name), contents);
    private static XElement Prop(string name, string value) => new(Ns + "Property", new XAttribute("Name", name), new XAttribute("Value", value));
    private static OdxProgram Program(params XElement[] body) => OdxProgramReader.Read("// source header\n<?xml version='1.0'?>\n" +
        new XElement(Ns + "MetaModel", new XAttribute(XNamespace.Xmlns + "om", Ns), Node("ServiceDeclaration", "Service", Node("ServiceBody", "Body", body))) + "\n// generated code is not executed");
    private static IEnumerable<OdxNode> Flatten(IEnumerable<OdxNode> roots) => roots.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));
    private static OdxNode Find(OdxProgram program, string name) => Flatten(program.Roots).Single(node => node.Name == name);
    private static OdxExecutionFrame Frame(OdxProgram program) => new(program, new("odx", "test", DateTimeOffset.UtcNow),
        new([new("input.json", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string>())]), new() { Module = "odx-test" });
    private static Task Run(OdxProgram program, OdxExecutionBindings bindings, OdxExecutionFrame frame, CancellationToken ct = default)
        => new OdxProgramExecutor().ExecuteAsync(program, program.Roots[0], bindings, frame, ct);

    [Fact]
    public async Task HierarchyRoundTrips_AndBoundBranchesLoopsMessagesAndLocalCatchRunInOrder()
    {
        var program = Program(
            Node("VariableAssignment", "Initialize", Prop("Expression", "developer supplied initialization")),
            Node("Decision", "Route",
                Node("DecisionBranch", "First", Prop("Expression", "first condition"), Node("MessageAssignment", "Normalize")),
                Node("DecisionBranch", "Second", Prop("Expression", "second condition"), Node("Send", "WrongSend")),
                Node("DecisionBranch", "Else", Node("Send", "ElseSend"))),
            Node("While", "Items", Prop("Expression", "remaining items"), Node("MessageAssignment", "MapItem")),
            Node("Scope", "HandleFailure", Node("Send", "Fail"),
                Node("Catch", "CatchIo", Prop("ExceptionType", "System.IO.IOException"), Prop("ExceptionName", "io"), Node("VariableAssignment", "WrongCatch")),
                Node("Catch", "CatchError", Prop("ExceptionType", "System.Exception"), Prop("ExceptionName", "ex"), Node("VariableAssignment", "RecordFailure"))),
            Node("Send", "Final"));
        program = JsonSerializer.Deserialize<OdxProgram>(JsonSerializer.Serialize(program))!;
        Assert.Equal(1, program.Version);
        var node = Find(program, "First"); Assert.Equal("oid-First", node.SourceId); Assert.Equal("link-DecisionBranch", node.ParentLink);
        Assert.True(node.SourceLine >= 3); Assert.Equal(Find(program, "Route").Id, node.ParentId);
        Assert.Equal(new[] { "First", "Second", "Else" }, Find(program, "Route").Children.Select(child => child.Name));
        var bindings = new OdxExecutionBindings(); var visits = new List<string>(); var frame = Frame(program);
        foreach (var activity in Flatten(program.Roots).Where(n => n.ShapeType is "VariableAssignment" or "MessageAssignment" or "Send"))
            bindings.Activities[activity.Id] = (n, f, ct) => { visits.Add(n.Name); return Task.CompletedTask; };
        bindings.Activities[Find(program, "Initialize").Id] = (n, f, ct) => { f.Variables["index"] = 0; visits.Add(n.Name); return Task.CompletedTask; };
        bindings.Activities[Find(program, "Normalize").Id] = (n, f, ct) => { f.Messages["normalized"] = f.Batch; visits.Add(n.Name); return Task.CompletedTask; };
        bindings.Conditions[Find(program, "First").Id] = (n, f, ct) => Task.FromResult(true);
        bindings.Conditions[Find(program, "Second").Id] = (n, f, ct) => throw new InvalidOperationException("Second condition must not be evaluated after First matched.");
        bindings.Conditions[Find(program, "Items").Id] = (n, f, ct) => Task.FromResult(f.Variables["index"]!.GetValue<int>() < 3);
        bindings.Activities[Find(program, "MapItem").Id] = (n, f, ct) => { f.Variables["index"] = f.Variables["index"]!.GetValue<int>() + 1; visits.Add(n.Name); return Task.CompletedTask; };
        bindings.Activities[Find(program, "Fail").Id] = (n, f, ct) => throw new InvalidOperationException("synthetic failure");
        bindings.Activities[Find(program, "RecordFailure").Id] = (n, f, ct) => { Assert.Equal("synthetic failure", f.CurrentException!.Message); visits.Add(n.Name); return Task.CompletedTask; };
        await Run(program, bindings, frame);
        Assert.Equal(new[] { "Initialize", "Normalize", "MapItem", "MapItem", "MapItem", "RecordFailure", "Final" }, visits);
        Assert.Equal(3, frame.Variables["index"]!.GetValue<int>()); Assert.Same(frame.Batch, frame.Messages["normalized"]); Assert.Null(frame.CurrentException);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task FirstMatchElseAndZeroIterationSemantics(bool first, bool second)
    {
        var program = Program(Node("Decision", "D", Node("DecisionBranch", "A", Prop("Expression", "a"), Node("Send", "sendA")),
            Node("DecisionBranch", "B", Prop("Expression", "b"), Node("Send", "sendB")), Node("DecisionBranch", "Else", Node("Send", "sendElse"))),
            Node("While", "Zero", Prop("Expression", "false"), Node("Send", "never")));
        var bindings = new OdxExecutionBindings(); var visited = new List<string>();
        foreach (var node in Flatten(program.Roots).Where(node => node.ShapeType == "Send"))
            bindings.Activities[node.Id] = (n, f, ct) => { visited.Add(n.Name); return Task.CompletedTask; };
        bindings.Conditions[Find(program, "A").Id] = (n, f, ct) => Task.FromResult(first);
        bindings.Conditions[Find(program, "B").Id] = (n, f, ct) => Task.FromResult(second);
        bindings.Conditions[Find(program, "Zero").Id] = (n, f, ct) => Task.FromResult(false);
        await Run(program, bindings, Frame(program));
        Assert.Equal(first ? "sendA" : second ? "sendB" : "sendElse", Assert.Single(visited));
    }

    [Fact]
    public async Task EveryReachableBindingIsPrevalidated_BeforeEarlierActivitiesCanHaveSideEffects()
    {
        var program = Program(Node("Send", "first"), Node("Decision", "D", Node("DecisionBranch", "If", Prop("Expression", "false"), Node("Listen", "unsupported wait"))));
        var bindings = new OdxExecutionBindings(); var called = false;
        bindings.Activities[Find(program, "first").Id] = (n, f, ct) => { called = true; return Task.CompletedTask; };
        bindings.Conditions[Find(program, "If").Id] = (n, f, ct) => Task.FromResult(false);
        var error = await Assert.ThrowsAsync<NotSupportedException>(() => Run(program, bindings, Frame(program)));
        Assert.Contains("Listen", error.Message); Assert.False(called);
    }

    [Fact]
    public async Task TransactionScopeRequiresExplicitBinding_AndCannotBeBypassedBySubtreeExecution()
    {
        var program = Program(Node("Scope", "Transaction", Node("AtomicTransaction", "Atomic"), Node("Send", "inside")));
        var frame = Frame(program); var bindings = new OdxExecutionBindings(); var visits = new List<string>();
        bindings.Activities[Find(program, "inside").Id] = (n, f, ct) => { visits.Add("send"); return Task.CompletedTask; };
        await Assert.ThrowsAsync<NotSupportedException>(() => Run(program, bindings, frame)); Assert.Empty(visits);
        await Assert.ThrowsAsync<ArgumentException>(() => new OdxProgramExecutor().ExecuteAsync(program, Find(program, "inside"), bindings, frame, default));
        bindings.Transactions[Find(program, "Transaction").Id] = async (n, f, body, ct) => { visits.Add("begin"); await body(ct); visits.Add("commit"); };
        await Run(program, bindings, frame);
        Assert.Equal(new[] { "begin", "send", "commit" }, visits);
    }

    [Fact]
    public async Task CancellationAndLimitsBypassSourceCatches_AndSuspensionStopsFollowingActions()
    {
        var program = Program(Node("Scope", "Scope", Node("While", "Forever", Prop("Expression", "true")),
            Node("Catch", "Catch", Prop("ExceptionType", "System.Exception"), Node("Send", "catch"))), Node("Send", "after"));
        var bindings = new OdxExecutionBindings(); var called = false;
        foreach (var node in Flatten(program.Roots).Where(node => node.ShapeType == "Send"))
            bindings.Activities[node.Id] = (n, f, ct) => { called = true; return Task.CompletedTask; };
        bindings.Conditions[Find(program, "Forever").Id] = (n, f, ct) => Task.FromResult(true);
        await Assert.ThrowsAsync<OdxExecutionLimitException>(() => Run(program, bindings, Frame(program))); Assert.False(called);
        bindings.Conditions[Find(program, "Forever").Id] = (n, f, ct) => throw new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(program, bindings, Frame(program))); Assert.False(called);
        var suspend = Program(Node("Receive", "wait"), Node("Send", "later"));
        bindings = new();
        bindings.Activities[Find(suspend, "wait").Id] = (n, f, ct) => { f.Context.Suspend("explicit application-owned continuation"); return Task.CompletedTask; };
        bindings.Activities[Find(suspend, "later").Id] = (n, f, ct) => throw new InvalidOperationException("must not run");
        var frame = Frame(suspend); await Run(suspend, bindings, frame); Assert.Equal(FlowExecutionDisposition.Suspended, frame.Context.Disposition);
    }

    [Fact]
    public async Task InvalidProgramAndInvalidBranchOrderFailWithoutActivities()
    {
        var program = Program(Node("Decision", "bad", Node("DecisionBranch", "Else"), Node("DecisionBranch", "AfterElse", Prop("Expression", "true"))));
        await Assert.ThrowsAsync<ArgumentException>(() => Run(program, new(), Frame(program)));
        var body = program.Roots[0].Children[0];
        var malformed = program with { Roots = [program.Roots[0] with { Children = [body, body] }] };
        await Assert.ThrowsAsync<ArgumentException>(() => Run(malformed, new(), Frame(malformed)));
        foreach (var invalid in new[] { Node("Catch", "misplaced", Prop("ExceptionType", "System.Exception")), Node("DecisionBranch", "misplaced") })
        {
            var misplaced = Program(Node("Send", "first"), invalid);
            var bindings = new OdxExecutionBindings();
            bindings.Activities[Find(misplaced, "first").Id] = (n, f, ct) => throw new InvalidOperationException("preflight must happen first");
            await Assert.ThrowsAsync<ArgumentException>(() => Run(misplaced, bindings, Frame(misplaced)));
        }
    }

    [Fact]
    public async Task ServiceDeclarationsInitializeBeforeBody_AndNestedCatchFailuresRestoreOuterException()
    {
        var program = Program(Node("Scope", "Outer", Node("Send", "fail"),
            Node("Catch", "OuterCatch", Prop("ExceptionType", "System.Exception"),
                Node("Scope", "Inner", Node("Send", "failInner"),
                    Node("Catch", "InnerCatch", Prop("ExceptionType", "System.Exception"), Node("Send", "handleInner"))), Node("Send", "handleOuter"))));
        var root = program.Roots[0]; var port = new OdxNode(1000, root.Id, 0, "port", "PortDeclaration", "", new Dictionary<string, string> { ["Name"] = "latePort" }, []);
        program = program with { Roots = [root with { Children = root.Children.Concat(new[] { port }).ToArray() }] };
        var bindings = new OdxExecutionBindings(); var visits = new List<string>();
        bindings.Activities[1000] = (n, f, ct) => { visits.Add("declare"); return Task.CompletedTask; };
        bindings.Activities[Find(program, "fail").Id] = (n, f, ct) => throw new InvalidOperationException("outer");
        bindings.Activities[Find(program, "failInner").Id] = (n, f, ct) => throw new IOException("inner");
        bindings.Activities[Find(program, "handleInner").Id] = (n, f, ct) => { Assert.Equal("inner", f.CurrentException!.Message); visits.Add("inner"); return Task.CompletedTask; };
        bindings.Activities[Find(program, "handleOuter").Id] = (n, f, ct) => { Assert.Equal("outer", f.CurrentException!.Message); visits.Add("outer"); return Task.CompletedTask; };
        var frame = Frame(program); await Run(program, bindings, frame);
        Assert.Equal(new[] { "declare", "inner", "outer" }, visits); Assert.Null(frame.CurrentException);
        bindings.Activities[Find(program, "handleInner").Id] = (n, f, ct) => throw new ArgumentException("handler failure");
        var error = await Assert.ThrowsAsync<ArgumentException>(() => Run(program, bindings, frame));
        Assert.Equal("handler failure", error.Message); Assert.Null(frame.CurrentException);
    }

    [Fact]
    public async Task NestedLoopsHaveGlobalBudget_AndSuspendedConditionsCannotEvaluateLaterBranches()
    {
        var program = Program(Node("While", "Outer", Prop("Expression", "true"), Node("While", "Inner", Prop("Expression", "counter < 100"))));
        var bindings = new OdxExecutionBindings(); var counter = 0;
        bindings.Conditions[Find(program, "Outer").Id] = (n, f, ct) => { counter = 0; return Task.FromResult(true); };
        bindings.Conditions[Find(program, "Inner").Id] = (n, f, ct) => Task.FromResult(counter++ < 100);
        var error = await Assert.ThrowsAsync<OdxExecutionLimitException>(() => Run(program, bindings, Frame(program)));
        Assert.Contains("100000", error.Message);
        var suspended = Program(Node("Decision", "D", Node("DecisionBranch", "First", Prop("Expression", "first")), Node("DecisionBranch", "Later", Prop("Expression", "later"))));
        bindings = new();
        bindings.Conditions[Find(suspended, "First").Id] = (n, f, ct) => { f.Context.Suspend("application-owned wait"); return Task.FromResult(false); };
        bindings.Conditions[Find(suspended, "Later").Id] = (n, f, ct) => throw new InvalidOperationException("must not evaluate after suspension");
        await Run(suspended, bindings, Frame(suspended));
    }

    [Fact]
    public async Task ReaderRejectsDtdOversizeDepthAndDuplicateProperties_AndHonorsFileCancellation()
    {
        Assert.Throws<ArgumentException>(() => OdxProgramReader.Read(new string('x', 8_000_001)));
        Assert.Throws<ArgumentException>(() => OdxProgramReader.Read("not ODX"));
        Assert.Throws<ArgumentException>(() => Program(Node("Send", "duplicate", Prop("Name", "again"))));
        Assert.Throws<XmlException>(() => OdxProgramReader.Read("<?xml version='1.0'?><!DOCTYPE MetaModel [<!ENTITY x SYSTEM 'file:///not-read'>]><MetaModel></MetaModel>"));
        Assert.Throws<ArgumentException>(() => OdxProgramReader.Read("<?xml version='1.0'?><MetaModel xmlns='wrong'><Element/></MetaModel>"));
        var nested = Node("Send", "deep"); for (var index = 0; index < 65; index++) nested = Node("Scope", "nested" + index, nested);
        Assert.Throws<ArgumentException>(() => Program(nested));
        Assert.Throws<ArgumentException>(() => Program(Enumerable.Range(0, 10001).Select(i => Node("Send", "many" + i)).ToArray()));
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "cancelled");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OdxProgramReader.ReadFileAsync(file, new CancellationToken(true)));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task BoundOdxRunsInsideRealFlow_WithDeclaredOutputAndDelivery()
    {
        var program = Program(Node("Decision", "Decision", Node("DecisionBranch", "If", Prop("Expression", "approved"), Node("MessageAssignment", "Build")),
            Node("DecisionBranch", "Else", Node("MessageAssignment", "Fallback"))));
        var bindings = new OdxExecutionBindings();
        bindings.Conditions[Find(program, "If").Id] = (n, f, ct) => Task.FromResult(f.Batch.Payloads[0].Metadata["approved"] == "true");
        foreach (var node in Flatten(program.Roots).Where(n => n.ShapeType == "MessageAssignment"))
            bindings.Activities[node.Id] = (n, f, ct) => { f.Batch = new(f.Batch.Payloads.Select(p => p with { Content = BinaryData.FromString(n.Name), ContentType = "text/plain" }).ToArray()); return Task.CompletedTask; };
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog().AddFetch(new DelegateFetchModule("fetch", (_, _, _, _) => Task.FromResult(new IntegrationBatch([
            new("in.json", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string> { ["approved"] = "true" })]))))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddAugment(new DelegateAugmentModule("bound-odx", async (context, batch, step, ct) =>
            {
                var frame = new OdxExecutionFrame(program, context, batch, step);
                await new OdxProgramExecutor().ExecuteAsync(program, program.Roots[0], bindings, frame, ct); return frame.Batch;
            }))
            .AddRender(DelegateModuleFactory.PassthroughRender("render")).AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
        var flow = new PipelineDefinition
        {
            Id = "odx", Enabled = true, Fetches = [new() { Id = "f", Fetch = new() { Module = "fetch" } }],
            Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
            Augments = [new() { Module = "bound-odx" }], Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }]
        };
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, new InMemoryFlowOrchestrationStateStore(), new InMemoryFlowRetryStateStore(),
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
        Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ExecuteAsync("odx", default)).Outcome);
        Assert.Equal("Build", Assert.Single(Assert.Single(delivered).Payloads).GetText());
    }
}
