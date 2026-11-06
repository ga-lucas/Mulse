using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeReceiveFilterImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-receive-filter-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    private const string Prefix = "{urn:properties}";
    private static (string Lhs, string Rhs, string Grouping, string Operator) Pred(string property, string? rhs = null, string grouping = "AND", string operation = "Equals")
        => (property.Contains('.') ? property : "Fixture.Properties." + property, rhs ?? "", grouping, operation);

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string? directBinding = "MessageBox", params (string Lhs, string Rhs, string Grouping, string Operator)[] predicates)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Fixture.btproj"), "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName><RootNamespace>Fixture</RootNamespace></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/><Schema Include='Message.xsd'/><Schema Include='Properties.xsd'><Namespace>Fixture.Properties</Namespace><TypeName>CoreProperties</TypeName></Schema></ItemGroup></Project>");
        string Property(string name, string type, string guid, string field) => $"<xs:element name=\"{name}\" type=\"xs:{type}\"><xs:annotation><xs:appinfo><b:fieldInfo propertyGuid=\"{guid}\" propSchFieldBase=\"{field}\"/></xs:appinfo></xs:annotation></xs:element>";
        await File.WriteAllTextAsync(Path.Combine(_directory, "Properties.xsd"), "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\" xmlns:b=\"http://schemas.microsoft.com/BizTalk/2003\" targetNamespace=\"urn:properties\"><xs:annotation><xs:appinfo><b:schemaInfo schema_type=\"property\"/></xs:appinfo></xs:annotation>"
            + Property("Flag", "boolean", "42e773b8-64c1-4aae-acaf-b3645a04f7d0", "MessageContextPropertyBase")
            + Property("Count", "int", "147d3ed8-a17c-4c12-98eb-3f9f93d9370f", "MessageContextPropertyBase")
            + Property("Text", "string", "c69469a7-426b-4b93-a555-5065c0951544", "MessageContextPropertyBase")
            + Property("Target", "string", "a69469a7-426b-4b93-a555-5065c0951545", "MessageContextPropertyBase") + "</xs:schema>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Message.xsd"), "<xs:schema xmlns:xs=\"http://www.w3.org/2001/XMLSchema\"><xs:element name=\"Request\"><xs:complexType><xs:sequence><xs:element name=\"Value\" type=\"xs:int\"/></xs:sequence></xs:complexType></xs:element></xs:schema>");
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var port = directBinding is null ? N("PortDeclaration", "input") : N("PortDeclaration", "input", N("DirectBindingAttribute", "binding", P("DirectBindingType", directBinding)));
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Flow", port, N("MessageDeclaration", "request", P("Type", "Fixture.Message")),
            N("ServiceBody", "Body", N("Receive", "input", P("MessageName", "request"), P("PortName", "input"),
                predicates.Select(item => N("DNFPredicate", "p", P("LHS", item.Lhs), P("RHS", item.Rhs), P("Grouping", item.Grouping), P("Operator", item.Operator))).Cast<object>().ToArray()))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new() { SourcePath = Path.Combine(_directory, "Fixture.btproj"), GenerateNativeFlow = true }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    private static NativeFlowNode Receive(NativeFlowDefinition definition) => definition.AllNodes().Single(node => node.Name == "input" && node.Kind is NativeFlowNodeKind.Receive or NativeFlowNodeKind.Blocked);
    private static IntegrationPayload Payload(params (string Key, string Value)[] metadata)
        => new("request.xml", BinaryData.FromString("<Request><Value>1</Value></Request>"), "application/xml", metadata.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));

    [Fact]
    public async Task AndFiltersBecomeTypedMetadataConditionsAndGateTheReceive()
    {
        var (candidate, definition) = await Import("MessageBox", Pred("Flag", "true"), Pred("Text", "\"ready\""), Pred("Count", "5"), Pred("Target", operation: "Exists"));
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.False(candidate.DraftFlow.Enabled);
        var receive = Receive(definition);
        Assert.Equal(NativeFlowNodeKind.Receive, receive.Kind); Assert.Equal("input", receive.Channel);
        Assert.Equal([Prefix + "Flag", Prefix + "Text", Prefix + "Count", Prefix + "Target"], receive.Conditions.Select(condition => condition.Path));
        Assert.Equal([DecisionComparisonOperator.Equals, DecisionComparisonOperator.Equals, DecisionComparisonOperator.Equals, DecisionComparisonOperator.Exists], receive.Conditions.Select(condition => condition.Operator));
        Assert.Equal(["true", "ready", "5", null], receive.Conditions.Select(condition => condition.Value));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("case-insensitive") && warning.Contains("Receive filters"));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("directly to the MessageBox"));
        Assert.DoesNotContain(definition.AllNodes(), node => node.Name.StartsWith("DNFPredicate", StringComparison.Ordinal));
        Assert.Contains(definition.AllNodes(), node => node.Name == "DirectBindingAttribute: binding");

        var good = Payload((Prefix + "Flag", "true"), (Prefix + "Text", "ready"), (Prefix + "Count", "5"), (Prefix + "Target", "x"));
        Assert.True(DecisionRuntime.MatchesAll(good, receive.Conditions, "r"));
        foreach (var bad in new[] { Payload((Prefix + "Flag", "false"), (Prefix + "Text", "ready"), (Prefix + "Count", "5"), (Prefix + "Target", "x")),
            Payload((Prefix + "Flag", "true"), (Prefix + "Text", "ready"), (Prefix + "Count", "6"), (Prefix + "Target", "x")),
            Payload((Prefix + "Flag", "true"), (Prefix + "Text", "ready"), (Prefix + "Count", "5")) })
            Assert.False(DecisionRuntime.MatchesAll(bad, receive.Conditions, "r"));
    }

    [Fact]
    public async Task OrAlternativesOnOnePropertyBecomeOneInCondition()
    {
        var (_, definition) = await Import("MessageBox", Pred("Text", "\"A\"", "OR"), Pred("Text", "\"B\"", "OR"), Pred("Text", "\"C D\"", "OR"));
        var condition = Assert.Single(Receive(definition).Conditions);
        Assert.Equal(DecisionComparisonOperator.In, condition.Operator); Assert.Equal(Prefix + "Text", condition.Path); Assert.Equal("A,B,C D", condition.Value);
        Assert.True(DecisionRuntime.MatchesAll(Payload((Prefix + "Text", "B")), [condition], "r"));
        Assert.False(DecisionRuntime.MatchesAll(Payload((Prefix + "Text", "C")), [condition], "r"));
    }

    [Theory]
    [InlineData("mixed")]
    [InlineData("orDifferentProperties")]
    [InlineData("orWithComma")]
    [InlineData("orWithPadding")]
    [InlineData("unequalOperator")]
    [InlineData("unquotedString")]
    [InlineData("typeMismatchBoolean")]
    [InlineData("badInteger")]
    [InlineData("existsWithOperand")]
    [InlineData("equalsWithoutOperand")]
    [InlineData("escapedString")]
    [InlineData("undeclaredProperty")]
    [InlineData("builtInUnlisted")]
    public async Task AnythingNotProvenStaysABlockedReceive(string scenario)
    {
        var predicates = scenario switch
        {
            "mixed" => new[] { Pred("Flag", "true"), Pred("Text", "\"A\"", "OR") },
            "orDifferentProperties" => new[] { Pred("Flag", "true", "OR"), Pred("Text", "\"A\"", "OR") },
            "orWithComma" => new[] { Pred("Text", "\"A,B\"", "OR"), Pred("Text", "\"C\"", "OR") },
            "orWithPadding" => new[] { Pred("Text", "\" A\"", "OR"), Pred("Text", "\"C\"", "OR") },
            "unequalOperator" => new[] { Pred("Count", "5", operation: "GreaterThan") },
            "unquotedString" => new[] { Pred("Text", "ready") },
            "typeMismatchBoolean" => new[] { Pred("Flag", "\"true\"") },
            "badInteger" => new[] { Pred("Count", "5.5") },
            "existsWithOperand" => new[] { Pred("Text", "\"A\"", operation: "Exists") },
            "equalsWithoutOperand" => new[] { Pred("Text") },
            "escapedString" => new[] { Pred("Text", "\"a\\\"b\"") },
            "undeclaredProperty" => new[] { Pred("Unknown", "\"A\"") },
            _ => new[] { Pred("BTS.MessageType", "\"A\"") }
        };
        var (candidate, definition) = await Import("MessageBox", predicates);
        var receive = Receive(definition);
        Assert.Equal(NativeFlowNodeKind.Blocked, receive.Kind); Assert.Contains("Receive filter is not translated", receive.BlockReason);
        Assert.False(candidate.DraftFlow.Enabled);
    }

    [Fact]
    public async Task BuiltInReceivePortNameFilterMapsToAnExplicitPlatformKey()
    {
        var (_, definition) = await Import("MessageBox", Pred("BTS.ReceivePortName", "\"In\""));
        var condition = Assert.Single(Receive(definition).Conditions);
        Assert.StartsWith("{" + NativeSourceContextCatalog.PlatformKeyNamespace + "BTS}", condition.Path); Assert.EndsWith("ReceivePortName", condition.Path);
    }

    [Theory]
    [InlineData("Self-correlating")]
    [InlineData("Partner orchestration")]
    [InlineData("")]
    public async Task OtherDirectBindingKindsStayBlocked(string kind)
    {
        var (_, definition) = await Import(kind);
        var block = definition.AllNodes().Single(node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains("DirectBindingAttribute", block.BlockReason);
    }

    [Fact]
    public async Task UnfilteredReceiveWithoutDirectBindingKeepsEmptyConditions()
    {
        var (_, definition) = await Import(null);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Empty(Receive(definition).Conditions);
    }

    [Fact]
    public async Task ExecutorRejectsNonMatchingMessagesWithoutAcceptingThem()
    {
        var (_, definition) = await Import("MessageBox", Pred("Flag", "true"));
        var store = new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };
        IntegrationBatch Batch(string flag) => new([new("request.xml", BinaryData.FromString("<Request><Value>1</Value></Request>"), "application/xml",
            new Dictionary<string, string>(StringComparer.Ordinal) { ["channel"] = "input", ["group"] = "A", ["messageId"] = "1", [Prefix + "Flag"] = flag })]);
        var module = new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store, TimeProvider.System);
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => module.AugmentAsync(new("ctx", "wrong", DateTimeOffset.UtcNow), Batch("false"), step, default));
        var context = new FlowExecutionContext("ctx", "right", DateTimeOffset.UtcNow);
        await module.AugmentAsync(context, Batch("true"), step, default);
        Assert.NotEqual(FlowExecutionDisposition.Suspended, context.Disposition);
    }

    private sealed class NoEffects : IAugmentModuleInvoker, IRenderModuleInvoker
    {
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No module should be invoked.");
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No module should be invoked.");
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
