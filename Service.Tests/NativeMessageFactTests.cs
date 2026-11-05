using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using System.Xml.XPath;
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

public sealed class NativeMessageFactTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-native-facts-" + Guid.NewGuid().ToString("N"));
    private const string Xml = "<b:Billing xmlns:b='urn:billing'><Details><Items><Item id='a'> first </Item><Item id='b'>second</Item></Items><Items xmlns='urn:wrong'><Item/></Items></Details></b:Billing>";
    private const string ItemPath = "/*[local-name()='Billing' and namespace-uri()='urn:billing']/*[local-name()='Details' and namespace-uri()='']/*[local-name()='Items' and namespace-uri()='']/*[local-name()='Item' and namespace-uri()='']";
    private static IntegrationBatch Input(string xml = Xml) => new([new("billing.xml", BinaryData.FromString(xml), "application/xml", new Dictionary<string, string>
        { ["group"] = "A", ["messageId"] = "1", ["channel"] = "input", ["Transport"] = "FILE" })]);
    private static NativeExpression Fact(NativeMessageFact fact) => new() { Kind = NativeExpressionKind.MessageValue, Fact = fact };
    private static ModuleStepDefinition Step(NativeFlowDefinition definition) => new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };

    [Theory]
    [InlineData("count", ItemPath, "2")]
    [InlineData("count", ItemPath + "/@id", "2")]
    [InlineData("string", ItemPath, " first ")]
    [InlineData("string", ItemPath + "/@id", "a")]
    [InlineData("boolean", ItemPath, "true")]
    [InlineData("count", "/Missing", "0")]
    [InlineData("string", "/Missing", "")]
    [InlineData("boolean", "/Missing", "false")]
    public void ExpandedNameFactsMatchXPathScalarResults(string function, string path, string expected)
    {
        var fact = NativeSourceXmlFactReader.Read("request", function + "(" + path + ")");
        var value = fact.Read(Input());
        Assert.Equal(expected, value.Value);
        var xpath = XDocument.Parse(Xml, LoadOptions.PreserveWhitespace).XPathEvaluate(function + "(" + path + ")");
        Assert.Equal(xpath is bool boolean ? boolean ? "true" : "false" : Convert.ToString(xpath, System.Globalization.CultureInfo.InvariantCulture), value.Value);
        NativeExpression.Validate(Fact(fact));
    }

    [Fact]
    public void XmlTextPreservesMixedWhitespaceAndNamespaceDeclarationsAreNotAttributes()
    {
        var fact = NativeSourceXmlFactReader.Read("", "string(/r)");
        Assert.Equal(" before  value  after ", fact.Read(Input("<r> before <i> value </i> after </r>")).Value);
        fact = NativeSourceXmlFactReader.Read("", "count(/r/@xmlns)");
        Assert.Equal("0", fact.Read(Input("<r xmlns:p='urn:p' />")).Value);
    }

    [Fact]
    public void MissingMetadataMessagesBatchAmbiguityAndInvalidXmlFailExplicitly()
    {
        var metadata = new NativeMessageFact { Kind = NativeMessageFactKind.Metadata, MetadataKey = "Transport" };
        Assert.Equal("FILE", metadata.Read(Input()).Value);
        metadata.MetadataKey = "transport";
        Assert.Throws<InvalidOperationException>(() => metadata.Read(Input()));
        Assert.Throws<InvalidOperationException>(() => metadata.Read(IntegrationBatch.Empty));
        Assert.Throws<InvalidOperationException>(() => metadata.Read(new([.. Input().Payloads, .. Input().Payloads])));
        var xml = NativeSourceXmlFactReader.Read("", "count(/r)");
        Assert.Throws<ArgumentException>(() => xml.Read(Input("<!DOCTYPE r [<!ENTITY x 'unsafe'>]><r>&x;</r>")));
        Assert.Throws<ArgumentException>(() => xml.Read(Input("<r>")));
        Assert.Throws<ArgumentException>(() => xml.Read(Input("<r>" + new string('x', 1_000_000) + "</r>")));
        Assert.Throws<ArgumentException>(() => NativeSourceXmlFactReader.Read("", "string(/r)").Read(Input("<r>" + new string('x', 4097) + "</r>")));
        Assert.Throws<InvalidOperationException>(() => Fact(xml).Evaluate(new Dictionary<string, NativeScalar>()));
        var inactive = new NativeExpression { Literal = NativeScalar.From(1), Fact = xml };
        Assert.Throws<ArgumentException>(() => NativeExpression.Validate(inactive));
        xml.Steps.Insert(0, new() { Name = "bad", Attribute = true });
        Assert.Throws<ArgumentException>(xml.Validate);
    }

    [Fact]
    public void FactLimitsAreEnforcedAtExactBoundariesAndShortCircuitSkipsMissingFacts()
    {
        var fact = new NativeMessageFact { Kind = NativeMessageFactKind.XmlCount, Steps = Enumerable.Range(0, 32).Select(_ => new NativeXmlStep { Name = "r" }).ToList() };
        fact.Validate();
        fact.Steps.Add(new() { Name = "extra" });
        Assert.Throws<ArgumentException>(fact.Validate);
        fact = new() { Kind = NativeMessageFactKind.Metadata, MetadataKey = "Transport" };
        Assert.Equal(4096, fact.Read(Input() with { Payloads = [Input().Payloads[0] with { Metadata = new Dictionary<string, string> { ["Transport"] = new('x', 4096) } }] }).Value.Length);
        Assert.Throws<ArgumentException>(() => fact.Read(Input() with { Payloads = [Input().Payloads[0] with { Metadata = new Dictionary<string, string> { ["Transport"] = new('x', 4097) } }] }));
        var expression = new NativeExpression { Kind = NativeExpressionKind.And, Operands = [new() { Literal = NativeScalar.From(false) }, Fact(fact)] };
        Assert.False(NativeExpression.Boolean(expression.Evaluate(new Dictionary<string, NativeScalar>(), _ => throw new InvalidOperationException("Must not read fact")).Read()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataFactsDriveTypedBranchesWithoutImplicitKeyAliases(bool durable)
    {
        var fact = new NativeMessageFact { Kind = NativeMessageFactKind.Metadata, MetadataKey = "Transport", MessageName = durable ? "request" : "" };
        var definition = new NativeFlowDefinition { Durable = durable, Nodes = [new()
        {
            Kind = NativeFlowNodeKind.If, Expression = new() { Kind = NativeExpressionKind.Equal, Operands = [Fact(fact), new() { Literal = NativeScalar.From("FILE") }] },
            Children = [new() { Kind = NativeFlowNodeKind.Render, Module = "selected" }]
        }] };
        if (durable) definition.Nodes.Insert(0, new() { Kind = NativeFlowNodeKind.Receive, MessageName = "request", Channel = "input" });
        var invoker = new Invoker();
        await new NativeControlFlowAugmentModule(invoker, invoker, new InMemoryFlowOrchestrationStateStore()).AugmentAsync(new("facts", "run", DateTimeOffset.UtcNow), Input(), Step(definition), default);
        Assert.Equal("selected", Assert.Single(invoker.Calls));
    }

    [Theory]
    [InlineData("count(//Item)")]
    [InlineData("count(/r/*)")]
    [InlineData("string(/r/i[1])")]
    [InlineData("string(/r/@id/child)")]
    [InlineData("count(/p:r)")]
    [InlineData("document('file:///secret')")]
    [InlineData("count(/r) + count(/secret)")]
    public void UnsupportedXPathIsRejectedNotExecuted(string xpath)
        => Assert.Throws<NotSupportedException>(() => NativeSourceXmlFactReader.Read("request", xpath));

    [Theory]
    [InlineData("count = System.Convert.ToInt32(xpath(request, \"count(/r)\")); Helper.Run();")]
    [InlineData("count = System.Convert.ToInt32(xpath(unknown, \"count(/r)\"));")]
    [InlineData("count = System.Convert.ToInt32(xpath(request, \"string(/r)\"));")]
    [InlineData("count = System.Convert.ToInt32(xpath(request, \"count(/r)\")); /* unfinished")]
    public void CompleteSourceStatementsNeverDropUnsupportedSuffixes(string source)
        => Assert.Throws<NotSupportedException>(() => new NativeSourceExpressionReader(source, new Dictionary<string, NativeScalarType> { ["count"] = NativeScalarType.Int32 }, new HashSet<string> { "request" }).ReadAssignments("source"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedCountsAndCommentedFinalAssignmentRunAgainstPersistedNamedMessage(bool overrideCount)
    {
        var statements = "count = System.Convert.ToInt32(xpath(request, \"count(" + ItemPath + "/@id)\"));\n" +
            "count = count + System.Convert.ToInt32(xpath(request, \"count(/Missing)\"));\n" +
            (overrideCount ? "// Intentional source override must not disappear.\ncount = 1;" : "/* end of complete block */");
        var candidate = await Import(statements);
        Assert.False(candidate.DraftFlow.Enabled);
        var definition = NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"]);
        var body = definition.AllNodes().Single(node => node.Name == "Body");
        body.Children.Insert(1, new() { Kind = NativeFlowNodeKind.Render, Module = "replace-working" });
        body.Children.Insert(2, new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5 });
        var expected = overrideCount ? 1 : 2;
        definition.Nodes.Add(new() { Kind = NativeFlowNodeKind.If, Expression = new() { Kind = NativeExpressionKind.Equal, Operands = [new() { Kind = NativeExpressionKind.Variable, Variable = "count" }, new() { Literal = NativeScalar.From(expected) }] },
            Children = [new() { Kind = NativeFlowNodeKind.Render, Module = "selected" }], Otherwise = [new() { Kind = NativeFlowNodeKind.Render, Module = "wrong" }] });
        IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
        var clock = new Clock(); var invoker = new Invoker();
        var first = new FlowExecutionContext("facts", "first", clock.Now);
        await new NativeControlFlowAugmentModule(invoker, invoker, Store(), clock).AugmentAsync(first, Input(), Step(definition), default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        var saved = Assert.Single(await Store().GetAllAsync("facts", default));
        Assert.Equal(Xml, Assert.Single(saved.Native!.State.Messages["request"]).Restore().GetText());
        clock.Now = clock.Now.AddSeconds(5);
        var resumed = new FlowExecutionContext("facts", "resumed", clock.Now);
        resumed.SetOrchestrationInstance(saved.InstanceId, saved.CorrelationKey, saved.Bookmark, true);
        await new NativeControlFlowAugmentModule(invoker, invoker, Store(), clock).AugmentAsync(resumed, IntegrationBatch.Empty, Step(definition), default);
        Assert.Equal(new[] { "replace-working", "selected" }, invoker.Calls);
        Assert.Equal(expected.ToString(), Assert.Single(await Store().GetAllAsync("facts", default)).Native!.State.Variables["count"].Value);
        var html = await Render(Step(definition).Settings["flowJson"]);
        Assert.Contains("Message fact", html); Assert.Contains("request", html); Assert.DoesNotContain("Original JSON is preserved", html);
    }

    [Fact]
    public async Task UnsupportedSourceBlockRetainsOriginalBlockerRatherThanPartialAssignments()
    {
        var candidate = await Import("count = System.Convert.ToInt32(xpath(request, \"count(/r)\")); Helper.Run();");
        var definition = NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true);
        var blocked = Assert.Single(definition.AllNodes(), node => node.Name == "counts");
        Assert.Equal(NativeFlowNodeKind.Blocked, blocked.Kind);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.SetVariable);
    }

    [Fact]
    public async Task WorkingMessageFactsExecuteInSynchronousForeachAndNamedFactsNeverFallback()
    {
        var fact = NativeSourceXmlFactReader.Read("", "count(/r/i)");
        var branch = new NativeFlowNode { Kind = NativeFlowNodeKind.If, Expression = new() { Kind = NativeExpressionKind.Equal,
            Operands = [Fact(fact), new() { Literal = NativeScalar.From(2) }] }, Children = [new() { Kind = NativeFlowNodeKind.Render, Module = "two" }], Otherwise = [new() { Kind = NativeFlowNodeKind.Render, Module = "other" }] };
        var definition = new NativeFlowDefinition { Nodes = [new() { Kind = NativeFlowNodeKind.ForEach, Children = [branch] }] };
        var invoker = new Invoker(); var module = new NativeControlFlowAugmentModule(invoker, invoker);
        await module.AugmentAsync(new("facts", "sync", DateTimeOffset.UtcNow), new([.. Input("<r><i/><i/></r>").Payloads, .. Input("<r/>").Payloads]), Step(definition), default);
        Assert.Equal(new[] { "two", "other" }, invoker.Calls);
        fact.MessageName = "missing";
        await Assert.ThrowsAsync<InvalidOperationException>(() => module.AugmentAsync(new("facts", "sync", DateTimeOffset.UtcNow), Input(), Step(definition), default));
        definition.Durable = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeControlFlowAugmentModule(invoker, invoker, new InMemoryFlowOrchestrationStateStore()).AugmentAsync(new("facts", "durable", DateTimeOffset.UtcNow), Input(), Step(definition), default));
        Assert.Equal(2, invoker.Calls.Count);
    }

    [Fact]
    public async Task SamplePathClickAuthoringRoundTripsWithoutSavingSampleValues()
    {
        var model = new NativeFlowEditorModel { Nodes = [new() { Kind = "DeclareVariable", Variable = "count", VariableType = "Int32", Expression = new() }] };
        var expression = model.Nodes[0].Expression!;
        var editor = new NativeExpressionEditor(); Set(editor, "Value", expression);
        await Invoke(editor, "ChangeKind", new ChangeEventArgs { Value = "MessageValue" });
        var facts = new NativeMessageFactEditor(); Set(facts, "Value", expression.Fact!);
        string saved = "";
        Set(facts, "Changed", EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(facts, "ChangeKind", new ChangeEventArgs { Value = "XmlCount" });
        Field(facts, "_sample").SetValue(facts, Xml);
        Method(facts, "InspectSample").Invoke(facts, null);
        var paths = (List<List<NativeXmlStepViewModel>>)Field(facts, "_paths").GetValue(facts)!;
        var selected = paths.Single(path => path.Count == 5 && path[^1].Attribute && path[^1].Name == "id");
        await Invoke(facts, "SelectPath", selected);
        var definition = NativeFlowDefinition.Read(saved);
        Assert.Equal("2", definition.Nodes[0].Expression!.Fact!.Read(Input()).Value);
        Assert.DoesNotContain("second", saved); Assert.DoesNotContain(" first ", saved);
        var html = await Render(saved);
        Assert.Contains("Choose XML path from a sample", html); Assert.DoesNotContain("Original JSON is preserved", html);
        await new NativeControlFlowAugmentModule(new Invoker(), new Invoker()).AugmentAsync(new("facts", "run", DateTimeOffset.UtcNow), Input(), Step(definition), default);
        Field(facts, "_sample").SetValue(facts, "<!DOCTYPE r [<!ENTITY x 'blocked'>]><r>&x;</r>");
        Method(facts, "InspectSample").Invoke(facts, null);
        Assert.Empty((List<List<NativeXmlStepViewModel>>)Field(facts, "_paths").GetValue(facts)!);
        Assert.NotNull(Field(facts, "_sampleError").GetValue(facts));
    }

    private async Task<BizTalkFlowCandidateResponse> Import(string statements)
    {
        Directory.CreateDirectory(_directory);
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Service",
            N("MessageDeclaration", "request"), N("VariableDeclaration", "count", P("Type", "System.Int32"), P("InitialValue", "0")),
            N("ServiceBody", "Body", N("Receive", "input", P("MessageName", "request"), P("PortName", "input")), N("VariableAssignment", "counts", P("Expression", statements)))));
        var project = Path.Combine(_directory, "Facts.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Facts</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Facts.odx'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Facts.odx"), "<?xml version='1.0'?>" + source);
        return Assert.Single((await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
    }
    private static MethodInfo Method(object instance, string name) => instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static FieldInfo Field(object instance, string name) => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static void Set(object instance, string name, object value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static async Task Invoke(object instance, string name, params object?[] args) => await (Task)Method(instance, name).Invoke(instance, args)!;
    private static async Task<string> Render(string json)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = json }))).ToHtmlString());
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Invoker : IAugmentModuleInvoker, IRenderModuleInvoker
    {
        public List<string> Calls { get; } = [];
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => RenderAsync(id, context, batch, settings, token);
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        { Calls.Add(id); return Task.FromResult(id == "replace-working" ? Input("<Other/>") : batch); }
    }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
