using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeScalarControlFlowTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-native-scalars-" + Guid.NewGuid().ToString("N"));
    private static NativeExpression Literal(object? value) => new() { Literal = NativeScalar.From(value) };
    private static NativeExpression Variable(string name) => new() { Kind = NativeExpressionKind.Variable, Variable = name };
    private static NativeExpression Op(NativeExpressionKind kind, params NativeExpression[] operands) => new() { Kind = kind, Operands = [.. operands] };
    private static ModuleStepDefinition Step(NativeFlowDefinition definition) => new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };

    [Theory]
    [InlineData("\"A\" == \"a\"", false)]
    [InlineData("null == null", true)]
    [InlineData("false && (1 / 0 == 0)", false)]
    [InlineData("true || unknown == 0", true)]
    [InlineData("1 + 2 * 3 == 7 && !(8 < 2)", true)]
    [InlineData("(1 + 2) * 3 == 9", true)]
    [InlineData("\"a&&b\" == \"a&&b\"", true)]
    [InlineData("\"\\\"x\\\"\" == \"\\\"x\\\"\"", true)]
    [InlineData("-5 % 2 == -1", true)]
    public void SourceCompilesToNativeTypedTreeWithExactSemantics(string source, bool expected)
    {
        var expression = new NativeSourceExpressionReader(source, new Dictionary<string, NativeScalarType> { ["unknown"] = NativeScalarType.Int32 }).ReadCondition();
        NativeExpression.Validate(expression);
        Assert.Equal(expected, NativeExpression.Boolean(expression.Evaluate(new Dictionary<string, NativeScalar>()).Read()));
        Assert.DoesNotContain("Odx", JsonSerializer.Serialize(expression, Json));
    }

    [Theory]
    [InlineData("System.IO.File.ReadAllText(\"x\") == \"x\"")]
    [InlineData("msg.Value == \"x\"")]
    [InlineData("unknown == 0")]
    [InlineData("true; Helper.Run();")]
    [InlineData("true ||")]
    [InlineData("2147483648 > 0")]
    [InlineData("\"bad\\q\" == \"x\"")]
    public void UnsupportedSourceIsNeverPartiallyConsumed(string source)
        => Assert.Throws<NotSupportedException>(() => new NativeSourceExpressionReader(source, new Dictionary<string, NativeScalarType>()).ReadCondition());

    [Fact]
    public void InvalidTypesOverflowAndExpressionBoundsFailExplicitly()
    {
        Assert.Throws<InvalidOperationException>(() => Op(NativeExpressionKind.Equal, Literal(1), Literal("1")).Evaluate(new Dictionary<string, NativeScalar>()));
        Assert.Throws<OverflowException>(() => Op(NativeExpressionKind.Add, Literal(int.MaxValue), Literal(1)).Evaluate(new Dictionary<string, NativeScalar>()));
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(new() { Kind = NativeExpressionKind.And, Operands = [Literal(true)] }));
        var root = Literal(true);
        for (var index = 0; index < 33; index++) root = Op(NativeExpressionKind.Not, root);
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(root));
        var cycle = Op(NativeExpressionKind.Not); cycle.Operands.Add(cycle);
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(cycle));
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(Literal(new string('x', 4097))));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    [InlineData(true, 2)]
    public async Task ImportedOrderedBranchesAndScalarLoopExecuteNativelyAcrossRestart(bool durable, int branch)
    {
        var candidate = await Import(branch);
        var step = new ModuleStepDefinition { Module = candidate.DraftFlow.Augments.Single().Module, Settings = new(candidate.DraftFlow.Augments.Single().Settings) };
        var definition = NativeFlowDefinition.Read(step.Settings["flowJson"]);
        definition.Durable = durable;
        if (!durable) definition.Nodes[0].Children.Single(node => node.Name == "Body").Children.RemoveAll(node => node.Kind is NativeFlowNodeKind.Wait or NativeFlowNodeKind.Receive);
        definition.Nodes.Add(new() { Kind = NativeFlowNodeKind.If, Expression = Op(NativeExpressionKind.Equal, Variable("chosen"), Literal(branch == 0 ? "first" : branch == 1 ? "second" : "else")),
            Children = [new() { Kind = NativeFlowNodeKind.Render, Module = "selected" }], Otherwise = [new() { Kind = NativeFlowNodeKind.Render, Module = "wrong" }] });
        step = Step(definition);
        Assert.Equal(3, definition.AllNodes().Count(node => node.Kind == NativeFlowNodeKind.If));
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains(definition.AllNodes(), node => node.Name == "else" && node.Kind == NativeFlowNodeKind.Sequence);
        var invoker = new NoEffects(); var clock = new Clock();
        IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(
            Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
        var module = new NativeControlFlowAugmentModule(invoker, invoker, Store(), clock);
        var payload = new IntegrationPayload("input.json", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string> { ["group"] = "A", ["messageId"] = "1", ["channel"] = "input" });
        var context = new FlowExecutionContext("native", "run", clock.Now);
        var result = await module.AugmentAsync(context, new([payload]), step, default);
        if (durable)
        {
            Assert.Equal(FlowExecutionDisposition.Suspended, context.Disposition);
            var waiting = Assert.Single(await Store().GetAllAsync("native", default));
            Assert.Equal("3", waiting.Native!.State.Variables["count"].Value);
            Assert.Equal("{}", Assert.Single(waiting.Native.State.Messages["request"]).Restore().GetText());
            Assert.Equal("1", Assert.Single(waiting.Native.State.AcceptedMessageIds));
            Assert.Equal(branch == 0 ? "first" : branch == 1 ? "second" : "else", waiting.Native.State.Variables["chosen"].Value);
            clock.Now = clock.Now.AddSeconds(5);
            context = new("native", "resumed", clock.Now);
            context.SetOrchestrationInstance(waiting.InstanceId, waiting.CorrelationKey, waiting.Bookmark, true);
            result = await new NativeControlFlowAugmentModule(invoker, invoker, Store(), clock).AugmentAsync(context, IntegrationBatch.Empty, step, default);
            var completed = Assert.Single(await Store().GetAllAsync("native", default));
            Assert.Equal("4", completed.Native!.State.Variables["count"].Value);
            Assert.Equal("Completed", completed.Native.State.Status);
        }
        Assert.Single(result.Payloads); Assert.Equal(new[] { "selected" }, invoker.Calls);
        var html = await Render(step.Settings["flowJson"]);
        Assert.Contains("Typed native expression", html); Assert.Contains("Declared type", html); Assert.Contains("count", html);
        Assert.DoesNotContain("Original JSON is preserved", html);
    }

    [Fact]
    public async Task ScalarInitializationDoesNotCheckpointOrAcceptUnmatchedInitialArrival()
    {
        var candidate = await Import();
        var step = new ModuleStepDefinition { Module = candidate.DraftFlow.Augments.Single().Module, Settings = new(candidate.DraftFlow.Augments.Single().Settings) };
        var store = new InMemoryFlowOrchestrationStateStore();
        var invoker = new NoEffects(); var clock = new Clock();
        var module = new NativeControlFlowAugmentModule(invoker, invoker, store, clock);
        var metadata = new Dictionary<string, string> { ["group"] = "A", ["messageId"] = "1", ["channel"] = "wrong" };
        IntegrationBatch Input() => new([new("input.json", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string>(metadata))]);
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => module.AugmentAsync(new("native", "rejected", clock.Now), Input(), step, default));
        Assert.Empty(await store.GetAllAsync("native", default));
        Assert.Empty(invoker.Calls);
        metadata["channel"] = "input";
        var accepted = new FlowExecutionContext("native", "accepted", clock.Now);
        await module.AugmentAsync(accepted, Input(), step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, accepted.Disposition);
        var saved = Assert.Single(await store.GetAllAsync("native", default));
        Assert.Equal("1", Assert.Single(saved.Native!.State.AcceptedMessageIds));
        Assert.Equal("3", saved.Native.State.Variables["count"].Value);
    }

    [Fact]
    public async Task MissingVariableDeclarationAndTypeChangesFailWithoutModuleCalls()
    {
        var invoker = new NoEffects(); var module = new NativeControlFlowAugmentModule(invoker, invoker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.AugmentAsync(new("n", "r", DateTimeOffset.UtcNow), IntegrationBatch.Empty,
            Step(new() { Nodes = [new() { Kind = NativeFlowNodeKind.SetVariable, Variable = "missing", Expression = Literal(1) }] }), default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.AugmentAsync(new("n", "r", DateTimeOffset.UtcNow), IntegrationBatch.Empty,
            Step(new() { Nodes = [new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "count", VariableType = NativeScalarType.Int32, Expression = Literal("wrong") }] }), default));
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task VisualExpressionAuthoringSavesAndReopensWithoutJsonEditing()
    {
        var model = new NativeFlowEditorModel(); var nodes = new NativeFlowNodesEditor();
        Set(nodes, nameof(nodes.Nodes), model.Nodes);
        string saved = "";
        Set(nodes, nameof(nodes.Changed), EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(nodes, "Add"); var node = model.Nodes.Single();
        await Invoke(nodes, "ChangeKind", node, "DeclareVariable"); node.Variable = "count"; node.VariableType = "Int32";
        var expression = new NativeExpressionEditor(); Set(expression, nameof(expression.Value), node.Expression!); Set(expression, nameof(expression.Changed), nodes.Changed);
        await Invoke(expression, "ChangeType", new ChangeEventArgs { Value = "Int32" }); node.Expression!.Literal!.Value = "2"; await Invoke(nodes, "Notify");
        await Invoke(nodes, "Add"); var branch = model.Nodes.Last(); await Invoke(nodes, "ChangeKind", branch, "If"); await Invoke(nodes, "ChangePredicate", branch, "Expression");
        Set(expression, nameof(expression.Value), branch.Expression!);
        await Invoke(expression, "ChangeKind", new ChangeEventArgs { Value = "Equal" });
        branch.Expression!.Operands[0] = new() { Kind = "Variable", Variable = "count", Literal = null };
        branch.Expression.Operands[1] = new() { Literal = new() { Type = "Int32", Value = "2" } };
        await Invoke(nodes, "Notify");
        var native = NativeFlowDefinition.Read(saved);
        Assert.Equal(NativeExpressionKind.Equal, native.Nodes[1].Expression!.Kind);
        Assert.Contains("case-sensitive", await Render(saved));
        await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects()).AugmentAsync(new("n", "r", DateTimeOffset.UtcNow), IntegrationBatch.Empty, Step(native), default);
    }

    [Fact]
    public async Task MalformedExpressionJsonIsPreservedWithoutRenderingUnsafeRecursion()
    {
        var definition = new NativeFlowDefinition { Nodes = [new() { Kind = NativeFlowNodeKind.If, Expression = Op(NativeExpressionKind.And, Literal(true)) }] };
        Assert.Contains("Original JSON is preserved", await Render(JsonSerializer.Serialize(definition, Json)));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json)));
    }

    [Fact]
    public async Task ExistingNativeDefinitionHashIsUnchangedWhenNewFieldsAreUnused()
    {
        const string previousJson = """
            {"version":1,"nodes":[{"id":"stable","name":"","kind":"Sequence","module":"","settings":{},"conditions":[],"maxIterations":100,"children":[],"otherwise":[],"blockReason":"","messageName":"","durationSeconds":0,"channel":"","correlationKeys":[]}],"durable":true,"correlationMetadataKey":"group","messageIdMetadataKey":"messageId","outputMessage":""}
            """;
        var expected = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("native-durable-v1\n" + previousJson)));
        var store = new InMemoryFlowOrchestrationStateStore();
        var module = new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store, TimeProvider.System);
        var payload = new IntegrationPayload("input", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string> { ["group"] = "A", ["messageId"] = "1" });
        await module.AugmentAsync(new("native", "run", DateTimeOffset.UtcNow), new([payload]),
            new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = previousJson } }, default);
        Assert.Equal(expected, Assert.Single(await store.GetAllAsync("native", default)).Native!.State.DefinitionHash);
    }

    [Fact]
    public async Task UnsupportedSourceConditionRetainsAllBranchBodiesAsExplicitBlockers()
    {
        var candidate = await Import(3);
        var draft = NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true);
        Assert.Contains(draft.AllNodes(), node => node.Name == "ordered" && node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains(draft.AllNodes(), node => node.Name == "first-set");
        Assert.Contains(draft.AllNodes(), node => node.Name == "second-set");
        Assert.Contains(draft.AllNodes(), node => node.Name == "else-set");
        Assert.Throws<NotSupportedException>(() => NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"]));
    }

    private async Task<BizTalkFlowCandidateResponse> Import(int branch = 1)
    {
        Directory.CreateDirectory(_directory);
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Service",
            N("VariableDeclaration", "count", P("Type", "System.Int32"), P("InitialValue", "0")),
            N("VariableDeclaration", "chosen", P("Type", "System.String"), P("InitialValue", "\"none\"")),
            N("ServiceBody", "Body",
                N("VariableAssignment", "before-receive", P("Expression", "count += 1;")),
                N("Receive", "input", P("MessageName", "request"), P("PortName", "input")),
                N("While", "counter", P("Expression", "count < 3"), N("VariableAssignment", "increment", P("Expression", "count++;"))),
                N("Decision", "ordered",
                    N("DecisionBranch", "first", P("Expression", branch == 3 ? "msg.Value == \"x\"" : branch == 0 ? "count >= 2" : "count == 2"), N("VariableAssignment", "first-set", P("Expression", "chosen = \"first\";"))),
                    N("DecisionBranch", "second", P("Expression", branch == 2 ? "false" : "count == 3 && \"A\" != \"a\""), N("VariableAssignment", "second-set", P("Expression", "chosen = \"second\";"))),
                    N("DecisionBranch", "else", N("VariableAssignment", "else-set", P("Expression", "chosen = \"else\";")))),
                N("Delay", "unbound"), N("VariableAssignment", "after", P("Expression", "count += 1;")))));
        var project = Path.Combine(_directory, "Native.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Native</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
        var native = NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true);
        var delay = native.AllNodes().Single(node => node.Name == "unbound");
        Assert.Equal(NativeFlowNodeKind.Blocked, delay.Kind);
        delay.Kind = NativeFlowNodeKind.Wait; delay.BlockReason = ""; delay.DurationSeconds = 5;
        return candidate with { DraftFlow = candidate.DraftFlow with { Augments = [new(NativeFlowDefinition.ModuleId, new Dictionary<string, string> { ["flowJson"] = JsonSerializer.Serialize(native, Json) })] } };
    }

    private static void Set(object instance, string name, object value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static async Task Invoke(object instance, string name, params object?[] args) => await (Task)instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, args)!;
    private static async Task<string> Render(string json)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = json }))).ToHtmlString());
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoEffects : IAugmentModuleInvoker, IRenderModuleInvoker
    {
        public List<string> Calls { get; } = [];
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) { Calls.Add(id); return Task.FromResult(batch); }
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) { Calls.Add(id); return Task.FromResult(batch); }
    }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
