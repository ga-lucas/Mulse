using System.Text.Json;
using System.Text.Json.Nodes;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;

namespace Service.Tests;

public sealed class NativeInvokeTransformTests
{
    private static NativeExpression Text(string value) => new() { Literal = NativeScalar.From(value) };
    private static NativeExpression Variable(string name) => new() { Kind = NativeExpressionKind.Variable, Variable = name };
    private static NativeFlowNode Declare(string name, NativeScalarType type, NativeExpression initial) => new()
    { Kind = NativeFlowNodeKind.DeclareVariable, Variable = name, VariableType = type, Expression = initial };
    private static NativeFlowNode Transform(string variable, params NativeExpression[] inputs) => new()
    { Kind = NativeFlowNodeKind.InvokeTransform, Module = "upper-udf", Variable = variable, TransformInputs = [.. inputs], Settings = new() { ["culture"] = "en-US" } };
    private static NativeFlowNode WhenEquals(string variable, string value, string module) => new()
    {
        Kind = NativeFlowNodeKind.If, Expression = new() { Kind = NativeExpressionKind.Equal, Operands = [Variable(variable), Text(value)] },
        Children = [new() { Kind = NativeFlowNodeKind.Render, Module = module }]
    };
    private static NativeFlowDefinition Flow(params NativeFlowNode[] nodes) => new() { Durable = true, Nodes = nodes.ToList() };
    private static IntegrationBatch Input() => new([new("one.json", BinaryData.FromString("{}"), "application/json",
        new Dictionary<string, string> { ["messageId"] = "one", ["group"] = "A" })]);
    private static ModuleStepDefinition Step(NativeFlowDefinition flow) => new()
    { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(flow, JsonSerializerOptions.Web) } };

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); }

    private sealed class Invoker : IAugmentModuleInvoker, IRenderModuleInvoker, IDeliverModuleInvoker, IModuleInvoker
    {
        public List<string> Calls { get; } = [];
        public List<string> TransformCalls { get; } = [];
        public Func<IReadOnlyList<JsonNode?>, JsonNode?> Transform { get; set; } = inputs => JsonValue.Create(inputs[0]?.GetValue<string>()?.ToUpperInvariant());
        public Dictionary<string, string> LastSettings { get; } = [];
        public Task<JsonNode?> InvokeTransformAsync(string id, FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        {
            TransformCalls.Add(id + ":" + string.Join("|", inputs.Select(input => input?.ToJsonString())));
            LastSettings.Clear(); foreach (var pair in settings) LastSettings[pair.Key] = pair.Value;
            return Task.FromResult(Transform(inputs));
        }
        public Task<IntegrationBatch> RenderAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        { Calls.Add(module); return Task.FromResult(batch); }
        public Task<IntegrationBatch> AugmentAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(module, context, batch, settings, token);
        public Task<IntegrationBatch> DeliverAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(module, context, batch, settings, token);
    }

    private static Task Run(Invoker invoker, NativeFlowDefinition flow, bool withTransforms = true)
    {
        var clock = new Clock();
        var module = new NativeControlFlowAugmentModule(invoker, invoker, new InMemoryFlowOrchestrationStateStore(), clock, invoker, withTransforms ? invoker : null);
        return module.AugmentAsync(new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.GetUtcNow()), Input(), Step(flow), default);
    }

    [Fact]
    public async Task TransformResultIsStoredInTheDeclaredStringVariableAndReceivesArgumentsAndSettings()
    {
        var invoker = new Invoker();
        await Run(invoker, Flow(Declare("name", NativeScalarType.String, Text("")), Transform("name", Text("abc"), new() { Literal = NativeScalar.From(7) }),
            WhenEquals("name", "ABC", "matched")));
        Assert.Equal(["upper-udf:\"abc\"|7"], invoker.TransformCalls);
        Assert.Equal("en-US", invoker.LastSettings["culture"]);
        Assert.Equal(["matched"], invoker.Calls);
    }

    [Fact]
    public async Task TransformRequiresADeclaredStringVariable()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Invoker(), Flow(Transform("undeclared", Text("a")))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(new Invoker(), Flow(Declare("count", NativeScalarType.Int32, new() { Literal = NativeScalar.From(0) }), Transform("count", Text("a")))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TransformMustReturnOneBoundedString(int shape)
    {
        var invoker = new Invoker { Transform = _ => shape == 0 ? null : shape == 1 ? JsonValue.Create(5) : JsonValue.Create(new string('x', 65_537)) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Run(invoker, Flow(Declare("name", NativeScalarType.String, Text("")), Transform("name", Text("a")))));
    }

    [Fact]
    public async Task MissingTransformInvokerFailsClosedBeforeAnyWork()
    {
        var invoker = new Invoker();
        await Assert.ThrowsAsync<NotSupportedException>(() => Run(invoker, Flow(new() { Kind = NativeFlowNodeKind.Render, Module = "first" },
            Declare("name", NativeScalarType.String, Text("")), Transform("name", Text("a"))), withTransforms: false));
        Assert.Empty(invoker.Calls);
    }

    [Fact]
    public async Task TryCatchesATransformFaultAndRunsTheHandler()
    {
        var invoker = new Invoker { Transform = _ => throw new InvalidOperationException("udf failed") };
        await Run(invoker, Flow(Declare("name", NativeScalarType.String, Text("")), new()
        {
            Kind = NativeFlowNodeKind.Try, ExceptionMessageVariable = "failure",
            Children = [Transform("name", Text("a")), new() { Kind = NativeFlowNodeKind.Render, Module = "skipped" }],
            Otherwise = [WhenEquals("failure", "udf failed", "handled")]
        }));
        Assert.Equal(["handled"], invoker.Calls);
    }

    [Fact]
    public void ValidationRequiresDurableFlowBoundedArgumentsAndAResultVariable()
    {
        string Json(NativeFlowDefinition flow) => JsonSerializer.Serialize(flow, JsonSerializerOptions.Web);
        NativeFlowDefinition.Read(Json(Flow(Declare("name", NativeScalarType.String, Text("")), Transform("name", Text("a")))));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(new NativeFlowDefinition { Nodes = [Transform("name", Text("a"))] })));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(Transform("1bad", Text("a"))))));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(Transform("name")))));
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(Transform("name", Enumerable.Repeat(Text("a"), 9).ToArray())))));
        var noModule = Transform("name", Text("a")); noModule.Module = "";
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(noModule))));
        var misplaced = new NativeFlowNode { Kind = NativeFlowNodeKind.Sequence, TransformInputs = [Text("a")] };
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Json(Flow(misplaced))));
    }

    [Fact]
    public void DependenciesReportTransformModulesForNativeFlows()
    {
        var flow = new PipelineDefinition { Id = "native", Enabled = true, Augments = [Step(Flow(Declare("name", NativeScalarType.String, Text("")), Transform("name", Text("a"))))] };
        Assert.Contains(("upper-udf", ModuleKind.Transform), Service.Flows.FlowModuleDependencies.References(flow));
    }
}
