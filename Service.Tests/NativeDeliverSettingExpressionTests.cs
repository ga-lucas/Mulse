using System.Text.Json;
using System.Text.Json.Nodes;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;

namespace Service.Tests;

public sealed class NativeDeliverSettingExpressionTests
{
    private static NativeExpression Text(string value) => new() { Literal = NativeScalar.From(value) };
    private static NativeExpression Variable(string name) => new() { Kind = NativeExpressionKind.Variable, Variable = name };
    private static NativeFlowNode Declare(string name, NativeScalarType type, NativeExpression initial) => new()
    { Kind = NativeFlowNodeKind.DeclareVariable, Variable = name, VariableType = type, Expression = initial };
    private static NativeFlowNode Deliver(Dictionary<string, NativeExpression> expressions, Dictionary<string, string>? settings = null) => new()
    { Kind = NativeFlowNodeKind.Deliver, Module = "sftp-deliver", Settings = settings ?? [], SettingExpressions = expressions };
    private static NativeFlowDefinition Flow(params NativeFlowNode[] nodes) => new() { Durable = true, Nodes = nodes.ToList() };
    private static string Json(NativeFlowDefinition flow) => JsonSerializer.Serialize(flow, JsonSerializerOptions.Web);
    private static IntegrationBatch Input() => new([new("one.json", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string> { ["messageId"] = "one", ["group"] = "A" })]);
    private static ModuleStepDefinition Step(NativeFlowDefinition flow) => new()
    { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = Json(flow) } };

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); }

    private sealed class Invoker : IAugmentModuleInvoker, IRenderModuleInvoker, IDeliverModuleInvoker, IModuleInvoker
    {
        public List<string> Calls { get; } = [];
        public List<Dictionary<string, string>> Settings { get; } = [];
        public Task<JsonNode?> InvokeTransformAsync(string id, FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new NotSupportedException();
        public Task<IntegrationBatch> RenderAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        { Calls.Add(module); Settings.Add(new(settings, StringComparer.OrdinalIgnoreCase)); return Task.FromResult(batch); }
        public Task<IntegrationBatch> AugmentAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(module, context, batch, settings, token);
        public Task<IntegrationBatch> DeliverAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(module, context, batch, settings, token);
    }

    private static Task Run(Invoker invoker, NativeFlowDefinition flow)
    {
        var clock = new Clock();
        var module = new NativeControlFlowAugmentModule(invoker, invoker, new InMemoryFlowOrchestrationStateStore(), clock, invoker);
        return module.AugmentAsync(new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.GetUtcNow()), Input(), Step(flow), default);
    }

    [Fact]
    public async Task ComputedValuesOverlayStaticSettingsAsInvariantText()
    {
        var invoker = new Invoker();
        await Run(invoker, Flow(
            Declare("host", NativeScalarType.String, Text("sftp.example.test")),
            Declare("port", NativeScalarType.Int32, new() { Literal = NativeScalar.From(2222) }),
            Declare("overwrite", NativeScalarType.Boolean, new() { Literal = NativeScalar.From(true) }),
            Deliver(new() { ["host"] = Variable("host"), ["port"] = Variable("port"), ["overwrite"] = Variable("overwrite") }, new() { ["username"] = "svc" })));
        var settings = Assert.Single(invoker.Settings);
        Assert.Equal("sftp.example.test", settings["host"]);
        Assert.Equal("2222", settings["port"]);
        Assert.Equal("true", settings["overwrite"]);
        Assert.Equal("svc", settings["username"]);
    }

    [Fact]
    public async Task ADeliverWithoutExpressionsKeepsItsStaticSettingsOnly()
    {
        var invoker = new Invoker();
        await Run(invoker, Flow(new NativeFlowNode { Kind = NativeFlowNodeKind.Deliver, Module = "sftp-deliver", Settings = new() { ["host"] = "static" } }));
        Assert.Equal("static", Assert.Single(invoker.Settings)["host"]);
    }

    [Fact]
    public async Task AFailingComputedValueSkipsTheModuleAndCanBeCaughtByTry()
    {
        var invoker = new Invoker();
        var failing = new NativeExpression { Kind = NativeExpressionKind.Substring, Operands = [Text("ab"), new() { Literal = NativeScalar.From(10) }] };
        await Run(invoker, Flow(new NativeFlowNode
        {
            Kind = NativeFlowNodeKind.Try, ExceptionMessageVariable = "failure",
            Children = [Deliver(new() { ["host"] = failing })],
            Otherwise = [new() { Kind = NativeFlowNodeKind.Render, Module = "handled" }]
        }));
        Assert.Equal(["handled"], invoker.Calls);
    }
    [Fact]
    public void ValidationAllowsOnlyDistinctNonStaticKeysOnDeliverNodes()
    {
        NativeFlowDefinition.Read(Json(Flow(Deliver(new() { ["host"] = Text("a") }, new() { ["username"] = "u" }))));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(Deliver(new() { ["host"] = Text("a") }, new() { ["Host"] = "u" })))));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(Deliver(new() { ["host"] = Text("a"), ["HOST"] = Text("b") })))));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(Deliver(new())))));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(Deliver(new() { [" "] = Text("a") })))));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(Deliver(Enumerable.Range(0, 33).ToDictionary(i => "k" + i, _ => Text("a")))))));
        var misplaced = new NativeFlowNode { Kind = NativeFlowNodeKind.Render, Module = "m", SettingExpressions = new() { ["host"] = Text("a") } };
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(misplaced))));
    }
}
