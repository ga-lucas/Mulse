using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mulse.Modules.Orchestration;

namespace Service.Tests;

public sealed class OdxAutomaticTranslationTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement Node(string type, string name, string? expression = null, params XElement[] children) => new(Ns + "Element",
        new XAttribute("Type", type), new XElement(Ns + "Property", new XAttribute("Name", "Name"), new XAttribute("Value", name)),
        expression is null ? null : new XElement(Ns + "Property", new XAttribute("Name", "Expression"), new XAttribute("Value", expression)), children);
    private static OdxProgram Program(params XElement[] body) => OdxProgramReader.Read("<?xml version='1.0'?>" + new XElement(Ns + "MetaModel",
        Node("Module", "M", null, Node("ServiceDeclaration", "Service", null, Node("ServiceBody", "Body", null, body)))));
    private static OdxExecutionFrame Frame(OdxProgram? program = null)
        => new(program ?? Program(), new("auto", "run", DateTimeOffset.UtcNow), IntegrationBatch.Empty, new());

    [Theory]
    [InlineData("count > 0 && msg.Config.Enabled == true", true)]
    [InlineData("count == 0 || (msg.Config.Kind != null && msg.Config.Kind != \"\")", true)]
    [InlineData("!(count > 2) && count * 2 + 1 == 3", true)]
    [InlineData("count / 2 == 0 && count % 2 == 1", true)]
    [InlineData("msg.Config.Kind == \"B\" || count >= 2", false)]
    [InlineData("/* comment */ msg.Config.Kind == \"A\" // ignored\n && count <= 1", true)]
    [InlineData("false && unavailable.Member == null", false)]
    [InlineData("true || unavailable.Member == null", true)]
    [InlineData("\"true\" == \"true\" && \"a\\\"b\" != \"a\"", true)]
    public async Task CompilesTypedConditionsWithPrecedenceCommentsEscapesAndShortCircuit(string expression, bool expected)
    {
        var frame = Frame(); frame.Variables["count"] = 1;
        frame.Variables["msg"] = JsonNode.Parse("{\"Config\":{\"Enabled\":true,\"Kind\":\"A\"}}");
        Assert.Equal(expected, await OdxExpressionCompiler.CompileCondition(expression)(frame.Program.Roots[0], frame, default));
    }

    [Fact]
    public async Task RegexAndFullyQualifiedPromotedPropertiesCompileWithoutSchemaNameGuessing()
    {
        var frame = Frame(); frame.Variables["pattern"] = "^ftp://[^/]+/";
        frame.Messages["msg"] = new([new("input", BinaryData.FromString("<Input/>"), "application/xml", new Dictionary<string, string>
        { ["Example.Properties.Target"] = "ftp://example.invalid/path", ["Another.Properties.Target"] = "wrong" })]);
        var condition = OdxExpressionCompiler.CompileCondition("System.Text.RegularExpressions.Regex.IsMatch(msg(Example.Properties.Target), pattern)");
        Assert.True(await condition(frame.Program.Roots[0], frame, default));
        frame.Messages["msg"] = IntegrationBatch.Empty;
        await Assert.ThrowsAsync<InvalidOperationException>(() => condition(frame.Program.Roots[0], frame, default));
    }

    [Fact]
    public async Task RegexEvaluationEnforcesItsTimeout()
    {
        var frame = Frame(); frame.Variables["input"] = new string('a', 25000) + "!";
        var condition = OdxExpressionCompiler.CompileCondition("Regex.IsMatch(input, \"^(a+)+$\")");
        await Assert.ThrowsAsync<System.Text.RegularExpressions.RegexMatchTimeoutException>(() => condition(frame.Program.Roots[0], frame, default));
    }

    [Fact]
    public async Task StatementsCompileCompletelyBeforeMutations_AndNeverRunUnsupportedCalls()
    {
        var frame = Frame(); frame.Variables["count"] = 0; frame.Variables["text"] = "";
        frame.Variables["facts"] = JsonNode.Parse("{\"Count\":0}");
        var activity = OdxExpressionCompiler.CompileStatements("count = 1; count++; count += 3; count--; text = \"C\" + count; facts.Count = count;");
        await activity(frame.Program.Roots[0], frame, default);
        Assert.Equal(4, frame.Variables["count"]!.GetValue<int>()); Assert.Equal("C4", frame.Variables["text"]!.GetValue<string>());
        Assert.Equal(4, frame.Variables["facts"]!["Count"]!.GetValue<int>());
        Assert.Throws<NotSupportedException>(() => OdxExpressionCompiler.CompileStatements("count = 99; System.IO.File.Delete(\"never\");"));
        Assert.Equal(4, frame.Variables["count"]!.GetValue<int>());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => activity(frame.Program.Roots[0], frame, new CancellationToken(true)));
    }

    [Theory]
    [InlineData("System.IO.File.Exists(\"never\")")]
    [InlineData("true || Dangerous.Call()")]
    [InlineData("new Object() == null")]
    [InlineData("a == \"unterminated")]
    [InlineData("a == @\"verbatim\"")]
    [InlineData("a == \"\\u0041\"")]
    [InlineData("a == \"x\"; b = 1;")]
    [InlineData("a ? true : false")]
    [InlineData("/* unclosed")]
    [InlineData("a.0invalid == 1")]
    public void UnsupportedSyntaxNeverBecomesAPartialCondition(string expression)
        => Assert.Throws<NotSupportedException>(() => OdxExpressionCompiler.CompileCondition(expression));

    [Fact]
    public async Task InvalidTypesMissingVariablesAndOverflowAreExplicit_NotFalseOrSilentDefaults()
    {
        var frame = Frame(); frame.Variables["count"] = int.MaxValue; frame.Variables["text"] = "1";
        await Assert.ThrowsAsync<InvalidOperationException>(() => OdxExpressionCompiler.CompileCondition("unknown == null")(frame.Program.Roots[0], frame, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OdxExpressionCompiler.CompileCondition("count == text")(frame.Program.Roots[0], frame, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OdxExpressionCompiler.CompileCondition("count")(frame.Program.Roots[0], frame, default));
        await Assert.ThrowsAsync<OverflowException>(() => OdxExpressionCompiler.CompileStatements("count++;")(frame.Program.Roots[0], frame, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => OdxExpressionCompiler.CompileStatements("undeclared = 1;")(frame.Program.Roots[0], frame, default));
    }

    [Fact]
    public void ParserAndEvaluationChainsAreBounded()
    {
        Assert.Throws<NotSupportedException>(() => OdxExpressionCompiler.CompileCondition(new string(' ', 65537)));
        Assert.Throws<NotSupportedException>(() => OdxExpressionCompiler.CompileCondition(new string('(', 65) + "true" + new string(')', 65)));
        Assert.Throws<NotSupportedException>(() => OdxExpressionCompiler.CompileCondition(string.Join(" && ", Enumerable.Repeat("true", 258))));
    }

    [Fact]
    public async Task AutomaticBindingsExecuteActualNestedStatementTree_NotFlatMetadataTags()
    {
        var program = Program(Node("VariableAssignment", "Start", "count = 0;"),
            Node("While", "Loop", "count < 3", Node("VariableAssignment", "Increment", "count++;")),
            Node("Decision", "Route", null,
                Node("DecisionBranch", "First", "count == 3", Node("VariableAssignment", "Choose", "text = \"first\";")),
                Node("DecisionBranch", "Second", "count >= 0", Node("VariableAssignment", "Wrong", "text = \"wrong\";")),
                Node("DecisionBranch", "Else", null, Node("VariableAssignment", "Fallback", "text = \"else\";"))));
        var translated = OdxAutomaticBindings.Compile(program); translated.EnsureComplete();
        var frame = Frame(program); frame.Variables["count"] = 42; frame.Variables["text"] = "";
        var entry = program.Roots[0].Children[0];
        await new OdxProgramExecutor().ExecuteAsync(program, entry, translated.Bindings, frame, default);
        Assert.Equal(3, frame.Variables["count"]!.GetValue<int>()); Assert.Equal("first", frame.Variables["text"]!.GetValue<string>());
        Assert.Equal(8, translated.Diagnostics.Count(diagnostic => diagnostic.Status == "Translated"));
    }

    [Fact]
    public async Task HelpersWaitsAndTransactionMarkersRemainExplicitBlockersBeforeAnyExecution()
    {
        var program = Program(Node("VariableAssignment", "Before", "count = 9;"), Node("MessageAssignment", "Helper", "Helper.Convert(msg);"),
            Node("Receive", "Receive"), Node("Delay", "Wait", "timeout"), Node("Scope", "Tx", null, Node("AtomicTransaction", "Atomic")));
        var translated = OdxAutomaticBindings.Compile(program);
        Assert.Equal(4, translated.Diagnostics.Count(diagnostic => diagnostic.Status == "RequiresBinding"));
        Assert.Throws<NotSupportedException>(translated.EnsureComplete);
        var frame = Frame(program); frame.Variables["count"] = 0;
        await Assert.ThrowsAsync<NotSupportedException>(() => new OdxProgramExecutor().ExecuteAsync(program, program.Roots[0].Children[0], translated.Bindings, frame, default));
        Assert.Equal(0, frame.Variables["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task CompletenessUsesCurrentExplicitBindingsAndParentOwnedMetadata_WithoutRunningEffects()
    {
        var program = Program(Node("Scope", "Scope", null, Node("AtomicTransaction", "Tx"),
            Node("Receive", "Receive", null, Node("DNFPredicate", "Filter")), Node("VariableAssignment", "After", "count++;")));
        var compilation = OdxAutomaticBindings.Compile(program);
        var scope = program.Roots[0].Children[0].Children[0].Children[0];
        var receive = scope.Children[1]; var invoked = 0;
        compilation.Bindings.Activities[receive.Id] = (_, frame, _) => { frame.Variables["count"] = 4; invoked++; return Task.CompletedTask; };
        Assert.Throws<NotSupportedException>(compilation.EnsureComplete);
        compilation.Bindings.Transactions[scope.Id] = async (_, _, body, token) => { invoked++; await body(token); };
        compilation.EnsureComplete();
        Assert.Equal(0, invoked);
        Assert.Equal(3, compilation.Diagnostics.Count(diagnostic => diagnostic.Status == "RequiresBinding"));
        var frame = Frame(program); frame.Variables["count"] = 0;
        await new OdxProgramExecutor().ExecuteAsync(program, program.Roots[0].Children[0], compilation.Bindings, frame, default);
        Assert.Equal(2, invoked); Assert.Equal(5, frame.Variables["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task AutomaticSourceConditionsAndStatementsRunInsideANativeFlow_WithoutPerNodeDelegates()
    {
        var program = Program(Node("VariableAssignment", "Start", "message.Count = 0;"),
            Node("While", "Count", "message.Count < 3", Node("VariableAssignment", "Increment", "message.Count++;")),
            Node("Decision", "Route", null,
                Node("DecisionBranch", "If", "message.Approved == true", Node("VariableAssignment", "Accept", "message.Status = \"accepted\";")),
                Node("DecisionBranch", "Else", null, Node("VariableAssignment", "Reject", "message.Status = \"rejected\";"))));
        var compilation = OdxAutomaticBindings.Compile(program); compilation.EnsureComplete();
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("fetch", (_, _, _, _) => Task.FromResult(new IntegrationBatch([
                new("input.json", BinaryData.FromString("{\"Count\":99,\"Approved\":true,\"Status\":\"pending\"}"), "application/json", new Dictionary<string, string>())]))))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddAugment(new DelegateAugmentModule("automatic-odx", async (context, batch, step, token) =>
            {
                var frame = new OdxExecutionFrame(program, context, batch, step);
                frame.Variables["message"] = JsonNode.Parse(Assert.Single(batch.Payloads).GetText());
                await new OdxProgramExecutor().ExecuteAsync(program, program.Roots[0].Children[0], compilation.Bindings, frame, token);
                return new IntegrationBatch([batch.Payloads[0] with { Content = BinaryData.FromString(frame.Variables["message"]!.ToJsonString()) }]);
            }))
            .AddRender(DelegateModuleFactory.PassthroughRender("render")).AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
        var flow = new PipelineDefinition
        {
            Id = "automatic-odx", Enabled = true,
            Fetches = [new() { Id = "f", Fetch = new() { Module = "fetch" } }],
            Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
            Augments = [new() { Module = "automatic-odx" }],
            Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }]
        };
        var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, new InMemoryFlowOrchestrationStateStore(), new InMemoryFlowRetryStateStore(),
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new())), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowRuntime>.Instance);
        Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ExecuteAsync(flow.Id, default)).Outcome);
        var output = JsonNode.Parse(Assert.Single(Assert.Single(delivered).Payloads).GetText())!;
        Assert.Equal(3, output["Count"]!.GetValue<int>()); Assert.Equal("accepted", output["Status"]!.GetValue<string>());
    }
}
