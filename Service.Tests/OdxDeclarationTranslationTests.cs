using System.Xml.Linq;
using Mulse.Modules.Orchestration;

namespace Service.Tests;

public sealed class OdxDeclarationTranslationTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement Property(string name, string value) => new(Ns + "Property", new XAttribute("Name", name), new XAttribute("Value", value));
    private static XElement Node(string type, string name, params object[] children) => new(Ns + "Element", new XAttribute("Type", type), Property("Name", name), children);
    private static XElement Declaration(string type, string? initial, string constructor = "False") => Node("VariableDeclaration", "value",
        Property("Type", type), Property("UseDefaultConstructor", constructor), Property("ParamDirection", "In"), initial is null ? Array.Empty<object>() : [Property("InitialValue", initial)]);
    private static OdxProgram Program(params XElement[] children) => OdxProgramReader.Read("<?xml version='1.0'?>" + new XElement(Ns + "MetaModel", Node("ServiceDeclaration", "Service", children)));
    private static OdxExecutionFrame Frame(OdxProgram program) => new(program, new("declarations", "run", DateTimeOffset.UtcNow), IntegrationBatch.Empty, new());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitServiceInitializerRunsBeforeItsBodyAndDoesNotRunAgainOnDurableCompletion(bool durable)
    {
        var program = Program(Node("ServiceBody", "Body", Node("VariableAssignment", "Increment", Property("Expression", "value++;"))), Declaration("System.Int32", "0"));
        var automatic = OdxAutomaticBindings.Compile(program); automatic.EnsureComplete(); var frame = Frame(program);
        if (durable)
        {
            var bindings = new OdxDurableBindings();
            foreach (var binding in automatic.Bindings.Activities) { bindings.Execution.Activities.Add(binding.Key, binding.Value); bindings.FrameLocalActivities.Add(binding.Key); }
            var executor = new OdxDurableExecutor(new InMemoryFlowOrchestrationStateStore(), TimeProvider.System);
            Assert.Equal(OdxDurableOutcome.Completed, (await executor.ExecuteAsync(program, program.Roots[0], bindings, frame, "instance", "v1", null, default)).Outcome);
            var restored = Frame(program);
            await executor.ExecuteAsync(program, program.Roots[0], bindings, restored, "instance", "v1", null, default);
            Assert.Equal(1, restored.Variables["value"]!.GetValue<int>());
        }
        else await new OdxProgramExecutor().ExecuteAsync(program, program.Roots[0], automatic.Bindings, frame, default);
        Assert.Equal(1, frame.Variables["value"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("System.String", "\"hello\"")]
    [InlineData("System.String", "null")]
    [InlineData("System.Boolean", "true")]
    [InlineData("System.Int32", "1 + 2")]
    public async Task ExplicitScalarInitializerIsTypeCheckedAndAutomaticallyBound(string type, string initial)
    {
        var program = Program(Declaration(type, initial), Node("ServiceBody", "Body"));
        var automatic = OdxAutomaticBindings.Compile(program); automatic.EnsureComplete(); var frame = Frame(program);
        await new OdxProgramExecutor().ExecuteAsync(program, program.Roots[0], automatic.Bindings, frame, default);
        Assert.True(frame.Variables.ContainsKey("value"));
        if (type == "System.Int32") Assert.Equal(3, frame.Variables["value"]!.GetValue<int>());
        else if (type == "System.Boolean") Assert.True(frame.Variables["value"]!.GetValue<bool>());
        else if (initial == "null") Assert.Null(frame.Variables["value"]);
        else Assert.Equal("hello", frame.Variables["value"]!.GetValue<string>());
        Assert.Contains(automatic.Diagnostics, diagnostic => diagnostic.ShapeType == "VariableDeclaration" && diagnostic.Status == "Translated");
    }

    [Theory]
    [InlineData("System.Int32", null, "False")]
    [InlineData("System.Xml.XmlDocument", null, "True")]
    [InlineData("System.Int32", "Dangerous.Call()", "False")]
    [InlineData("System.Type", "null", "False")]
    [InlineData("System.String", "\"hello\"", "True")]
    [InlineData("System.Int32", "0; value = 99;", "False")]
    public void MissingDefaultsConstructorsAndHelpersRemainExplicitBlockers(string type, string? initial, string constructor)
    {
        var program = Program(Declaration(type, initial, constructor), Node("ServiceBody", "Body"));
        var automatic = OdxAutomaticBindings.Compile(program);
        Assert.Contains(automatic.Diagnostics, diagnostic => diagnostic.ShapeType == "VariableDeclaration" && diagnostic.Status == "RequiresBinding");
        Assert.Throws<NotSupportedException>(() => automatic.EnsureComplete());
    }

    [Fact]
    public async Task WrongInitializerTypeDoesNotMutateFacts_AndDuplicateOrScopedDeclarationsAreNotSilentlyFlattened()
    {
        var program = Program(Declaration("System.Int32", "\"wrong type\""), Node("ServiceBody", "Body"));
        var frame = Frame(program); frame.Variables["value"] = 5;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new OdxProgramExecutor().ExecuteAsync(program, program.Roots[0], OdxAutomaticBindings.Compile(program).Bindings, frame, default));
        Assert.Equal(5, frame.Variables["value"]!.GetValue<int>());
        var duplicates = Program(Declaration("System.Int32", "0"), Declaration("System.Int32", "1"), Node("ServiceBody", "Body"));
        Assert.Throws<ArgumentException>(() => OdxAutomaticBindings.Compile(duplicates));
        var scoped = Program(Node("ServiceBody", "Body", Node("Scope", "Local", Declaration("System.Int32", "0"))));
        Assert.Throws<NotSupportedException>(() => OdxAutomaticBindings.Compile(scoped).EnsureComplete());
    }
}
