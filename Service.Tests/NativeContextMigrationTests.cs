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

public sealed class NativeContextMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-context-migration-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    private const string Prefix = "{urn:properties}";
    private static IntegrationBatch Input() => new([new("request.xml", BinaryData.FromString("<Request><Value>0</Value></Request>"), "application/xml", new Dictionary<string, string>(StringComparer.Ordinal)
        { ["group"] = "A", ["messageId"] = "1", ["channel"] = "input", [Prefix + "Flag"] = "1", [Prefix + "Count"] = " +0041 ", [Prefix + "Text"] = "  exact  " })]);
    private static NativeMessageFact Fact(NativeMessageFactKind kind, string key = "value") => new() { Kind = kind, MetadataKey = key };

    [Theory]
    [InlineData(NativeMessageFactKind.MetadataBoolean, "1", NativeScalarType.Boolean, "true")]
    [InlineData(NativeMessageFactKind.MetadataBoolean, " 0\r\n", NativeScalarType.Boolean, "false")]
    [InlineData(NativeMessageFactKind.MetadataBoolean, "false", NativeScalarType.Boolean, "false")]
    [InlineData(NativeMessageFactKind.MetadataInt32, " +0042 ", NativeScalarType.Int32, "42")]
    [InlineData(NativeMessageFactKind.MetadataInt32, "-2147483648", NativeScalarType.Int32, "-2147483648")]
    [InlineData(NativeMessageFactKind.Metadata, "1", NativeScalarType.String, "1")]
    [InlineData(NativeMessageFactKind.Metadata, "  exact  ", NativeScalarType.String, "  exact  ")]
    public void MetadataFactsHaveExplicitTypesAndXmlLexicalValues(NativeMessageFactKind kind, string text, NativeScalarType type, string expected)
    {
        var payload = Input().Payloads[0] with { Metadata = new Dictionary<string, string> { ["value"] = text } };
        var result = Fact(kind).Read(new([payload]));
        Assert.Equal(type, result.Type); Assert.Equal(expected, result.Value);
        Assert.DoesNotContain("isMetadata", JsonSerializer.Serialize(Fact(kind), Json));
    }

    [Theory]
    [InlineData(NativeMessageFactKind.MetadataBoolean, "True")]
    [InlineData(NativeMessageFactKind.MetadataBoolean, "yes")]
    [InlineData(NativeMessageFactKind.MetadataBoolean, "")]
    [InlineData(NativeMessageFactKind.MetadataInt32, "1.0")]
    [InlineData(NativeMessageFactKind.MetadataInt32, "2147483648")]
    [InlineData(NativeMessageFactKind.MetadataInt32, "1,000")]
    public void InvalidTypedMetadataNeverCoercesOrDefaults(NativeMessageFactKind kind, string text)
    {
        var payload = Input().Payloads[0] with { Metadata = new Dictionary<string, string> { ["value"] = text } };
        Assert.Throws<ArgumentException>(() => Fact(kind).Read(new([payload])));
    }

    [Fact]
    public void ExactMetadataContractRejectsWrongCaseMissingKeysInactiveXmlAndOversizedScalars()
    {
        var fact = Fact(NativeMessageFactKind.MetadataBoolean, Prefix + "flag");
        Assert.Throws<InvalidOperationException>(() => fact.Read(Input()));
        fact = Fact(NativeMessageFactKind.MetadataBoolean, Prefix + "Missing");
        Assert.Throws<InvalidOperationException>(() => fact.Read(Input()));
        fact.Steps.Add(new() { Name = "Value" });
        Assert.Throws<ArgumentException>(fact.Validate);
        fact = Fact(NativeMessageFactKind.MetadataInt32, new string('x', 2049));
        Assert.Throws<ArgumentException>(fact.Validate);
        var payload = Input().Payloads[0] with { Metadata = new Dictionary<string, string> { ["value"] = new('1', 4097) } };
        Assert.Throws<ArgumentException>(() => Fact(NativeMessageFactKind.MetadataInt32).Read(new([payload])));
        Assert.Throws<InvalidOperationException>(() => Fact(NativeMessageFactKind.MetadataBoolean).Read(IntegrationBatch.Empty));
    }

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string? expression = null, Action<XDocument>? editProperties = null, bool duplicate = false, bool sameKeyOtherNamespace = false)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Fixture.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName><RootNamespace>Fixture</RootNamespace></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/><Schema Include='Message.xsd'/><Schema Include='Properties.xsd'><Namespace>Fixture.Properties</Namespace><TypeName>CoreProperties</TypeName></Schema>" +
            (duplicate || sameKeyOtherNamespace ? "<Schema Include='Other.xsd'><Namespace>" + (sameKeyOtherNamespace ? "Other.Properties" : "Fixture.Properties") + "</Namespace><TypeName>OtherProperties</TypeName></Schema>" : "") + "</ItemGroup></Project>");
        var properties = XDocument.Parse("""
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:b="http://schemas.microsoft.com/BizTalk/2003" targetNamespace="urn:properties">
              <xs:annotation><xs:appinfo><b:schemaInfo schema_type="property"/></xs:appinfo></xs:annotation>
              <xs:element name="Flag" type="xs:boolean"><xs:annotation><xs:appinfo><b:fieldInfo propertyGuid="42e773b8-64c1-4aae-acaf-b3645a04f7d0" propSchFieldBase="MessageDataPropertyBase"/></xs:appinfo></xs:annotation></xs:element>
              <xs:element name="Count" type="xs:int"><xs:annotation><xs:appinfo><b:fieldInfo propertyGuid="147d3ed8-a17c-4c12-98eb-3f9f93d9370f" propSchFieldBase="MessageContextPropertyBase"/></xs:appinfo></xs:annotation></xs:element>
              <xs:element name="Text" type="xs:string"><xs:annotation><xs:appinfo><b:fieldInfo propertyGuid="c69469a7-426b-4b93-a555-5065c0951544" propSchFieldBase="MessageDataPropertyBase"/></xs:appinfo></xs:annotation></xs:element>
            </xs:schema>
            """);
        if (duplicate || sameKeyOtherNamespace) await File.WriteAllTextAsync(Path.Combine(_directory, "Other.xsd"), properties.ToString());
        editProperties?.Invoke(properties);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Properties.xsd"), properties.ToString());
        await File.WriteAllTextAsync(Path.Combine(_directory, "Message.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:b="http://schemas.microsoft.com/BizTalk/2003">
              <xs:element name="Request"><xs:annotation><xs:appinfo><b:properties><b:property distinguished="true" xpath="/*[local-name()='Request' and namespace-uri()='']/*[local-name()='Value' and namespace-uri()='']"/></b:properties></xs:appinfo></xs:annotation><xs:complexType><xs:sequence><xs:element name="Value" type="xs:int"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """);
        expression ??= "output = request; output(Fixture.Properties.Flag) = request(Fixture.Properties.Flag); output(Fixture.Properties.Count) = request(Fixture.Properties.Count) + 1; output(Fixture.Properties.Text) = request(Fixture.Properties.Text); output.Value = output(Fixture.Properties.Count);";
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Flow",
            N("MessageDeclaration", "request", P("Type", "Fixture.Message")), N("MessageDeclaration", "output", P("Type", "Fixture.Message")),
            N("ServiceBody", "Body", N("Receive", "input", P("MessageName", "request"), P("PortName", "input")),
                N("Construct", "construct", N("MessageAssignment", "fields", P("Expression", expression)), N("MessageRef", "result", P("Ref", "output"))),
                N("Decision", "branch", N("DecisionBranch", "enabled", P("Expression", "output(Fixture.Properties.Flag) && output(Fixture.Properties.Count) == 42")), N("DecisionBranch", "otherwise")),
                N("Send", "reply", P("MessageName", "output"), P("OperationMessageName", "Response")))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new() { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    [Fact]
    public async Task SchemaProvenContextReadsWritesAndDecisionsExecuteAcrossDiskRestartWithoutChangingSource()
    {
        var (candidate, definition) = await Import();
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.False(candidate.DraftFlow.Enabled);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("Incoming promotion/context adapters"));
        var body = definition.AllNodes().Single(node => node.Name == "Body");
        body.Children.Insert(body.Children.Count - 1, new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5 });
        var clock = new Clock();
        IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };
        var input = Input(); var context = new FlowExecutionContext("context", "first", clock.Now);
        var module = new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock);
        await module.AugmentAsync(context, input, step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, context.Disposition);
        var saved = Assert.Single(await Store().GetAllAsync("context", default));
        var state = saved.Native!.State;
        Assert.Equal("1", state.Messages["request"].Single().Restore().Metadata[Prefix + "Flag"]);
        Assert.Equal("true", state.Messages["output"].Single().Restore().Metadata[Prefix + "Flag"]);
        Assert.Equal("42", state.Messages["output"].Single().Restore().Metadata[Prefix + "Count"]);
        clock.Now = clock.Now.AddSeconds(5);
        var resumed = new FlowExecutionContext("context", "resumed", clock.Now);
        resumed.SetOrchestrationInstance(saved.InstanceId, saved.CorrelationKey, saved.Bookmark, true);
        await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(resumed, IntegrationBatch.Empty, step, default);
        var response = Assert.Single(resumed.CapturedResponses);
        Assert.Equal("42", XDocument.Parse(response.GetText()).Root!.Element("Value")!.Value);
        Assert.Equal("  exact  ", response.Metadata[Prefix + "Text"]);
        Assert.Equal(" +0041 ", input.Payloads[0].Metadata[Prefix + "Count"]);
        Assert.Equal("<Request><Value>0</Value></Request>", input.Payloads[0].GetText());
    }

    [Theory]
    [InlineData("output = request; output(Fixture.Properties.Unknown) = true;")]
    [InlineData("output = request; output(BTS.MessageID) = true;")]
    [InlineData("output = request; output(\"Fixture.Properties.Flag\") = true;")]
    [InlineData("output = request; output(Fixture.Properties.Flag) = true; Helper.Run();")]
    [InlineData("output = request; output.Part(Fixture.Properties.Flag) = true;")]
    public async Task UnprovenPropertiesMultipartDynamicIdentityAndUnsupportedSuffixRejectEntireBlock(string expression)
    {
        var (_, definition) = await Import(expression);
        var block = definition.AllNodes().Single(node => node.Name == "fields");
        Assert.Equal(NativeFlowNodeKind.Blocked, block.Kind); Assert.Empty(block.Children);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DuplicateClrPropertiesOrExpandedIdentitiesNeverPickAWinner(bool duplicate, bool sameKey)
    {
        var (_, definition) = await Import(duplicate: duplicate, sameKeyOtherNamespace: sameKey);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "fields").Kind);
    }

    [Theory]
    [InlineData("type", "xs:long")]
    [InlineData("type", "xs:dateTime")]
    [InlineData("propSchFieldBase", "UnknownBase")]
    [InlineData("propertyGuid", "not-a-guid")]
    public async Task UnsupportedPropertyBlocksItselfWithoutPoisoningOtherProvenFields(string attribute, string value)
    {
        var (_, definition) = await Import("output = request; output(Fixture.Properties.Text) = request(Fixture.Properties.Text);", properties =>
        {
            var flag = properties.Root!.Elements().Single(element => (string?)element.Attribute("name") == "Flag");
            if (attribute == "type") flag.SetAttributeValue(attribute, value);
            else flag.Descendants().Single(element => element.Name.LocalName == "fieldInfo").SetAttributeValue(attribute, value);
        });
        Assert.Equal(NativeFlowNodeKind.Sequence, definition.AllNodes().Single(node => node.Name == "fields").Kind);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "branch").Kind);
    }

    [Fact]
    public async Task MissingMetadataDoesNotReadSimilarlyNamedXmlBody()
    {
        var (_, definition) = await Import();
        var input = Input(); var payload = input.Payloads[0] with { Metadata = input.Payloads[0].Metadata.Where(pair => pair.Key != Prefix + "Flag").ToDictionary(pair => pair.Key, pair => pair.Value), Content = BinaryData.FromString("<Request><Value>0</Value><Flag>true</Flag></Request>") };
        var store = new InMemoryFlowOrchestrationStateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store).AugmentAsync(new("context", "first", DateTimeOffset.UtcNow), new([payload]),
            new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } }, default));
        Assert.Equal("Faulted", Assert.Single(await store.GetAllAsync("context", default)).Native!.State.Status);
    }

    [Theory]
    [InlineData(NativeMessageFactKind.MetadataBoolean, NativeScalarType.String, "true")]
    [InlineData(NativeMessageFactKind.MetadataInt32, NativeScalarType.String, "1")]
    [InlineData(NativeMessageFactKind.Metadata, NativeScalarType.Int32, "1")]
    [InlineData(NativeMessageFactKind.MetadataBoolean, NativeScalarType.Null, "")]
    public async Task TypedMetadataWritesRejectMismatchedValuesAndPreserveNamedSnapshot(NativeMessageFactKind kind, NativeScalarType type, string text)
    {
        var target = Fact(kind, Prefix + "Flag"); target.MessageName = "request";
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "request" }, new() { Kind = NativeFlowNodeKind.SetMessageValue, Target = target, Expression = new() { Literal = new(type, text) } }] };
        var store = new InMemoryFlowOrchestrationStateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store).AugmentAsync(new("context", "first", DateTimeOffset.UtcNow), Input(),
            new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } }, default));
        Assert.Equal("1", Assert.Single(await store.GetAllAsync("context", default)).Native!.State.Messages["request"].Single().Restore().Metadata[Prefix + "Flag"]);
    }

    [Fact]
    public async Task GraphicalTypedContextFactsRoundTripAndClearInactiveXmlSteps()
    {
        var (_, definition) = await Import();
        var json = JsonSerializer.Serialize(definition, Json);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = json }))).ToHtmlString());
        Assert.Contains("MetadataBoolean", html); Assert.Contains("MetadataInt32", html); Assert.Contains(Prefix + "Flag", html);
        Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete or incompatible", html);
        var value = new NativeMessageFactViewModel { Kind = "XmlInt32", MessageName = "request", Steps = [new() { Name = "Request" }, new() { Name = "Value" }] };
        var editor = new NativeMessageFactEditor();
        typeof(NativeMessageFactEditor).GetProperty("Value")!.SetValue(editor, value);
        var change = typeof(NativeMessageFactEditor).GetMethod("ChangeKind", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)change.Invoke(editor, [new ChangeEventArgs { Value = "MetadataBoolean" }])!;
        Assert.Empty(value.Steps); Assert.Equal("", value.MetadataKey);
        value.MetadataKey = Prefix + "Flag";
        var restored = JsonSerializer.Deserialize<NativeMessageFact>(JsonSerializer.Serialize(value, Json), Json)!;
        restored.Validate(); Assert.Equal(NativeMessageFactKind.MetadataBoolean, restored.Kind);
        Assert.Equal(Prefix + "Flag", restored.MetadataKey);
    }

    private async Task<NativeFlowDefinition> AnalyzeAgain()
    {
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new() { SourcePath = Path.Combine(_directory, "Fixture.btproj"), GenerateNativeFlow = true }, default)).FlowCandidates);
        return NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true);
    }

    [Theory]
    [InlineData("MessageDeclaration")]
    [InlineData("VariableDeclaration")]
    public async Task ScopedMessageOrVariableShadowingCannotSelectTheServiceMessageContext(string kind)
    {
        await Import();
        var path = Path.Combine(_directory, "Flow.odx"); var source = XDocument.Load(path);
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        source.Descendants(ns + "Element").Single(element => (string?)element.Attribute("Type") == "ServiceBody").Add(
            new XElement(ns + "Element", new XAttribute("Type", "Scope"),
                new XElement(ns + "Element", new XAttribute("Type", kind), new XElement(ns + "Property", new XAttribute("Name", "Name"), new XAttribute("Value", "request")),
                    new XElement(ns + "Property", new XAttribute("Name", "Type"), new XAttribute("Value", "Fixture.Message")))));
        await File.WriteAllTextAsync(path, "<?xml version='1.0'?>" + source);
        var definition = await AnalyzeAgain();
        var block = definition.AllNodes().Single(node => node.Name == "fields");
        Assert.Equal(NativeFlowNodeKind.Blocked, block.Kind); Assert.Contains("unshadowed", block.BlockReason); Assert.Empty(block.Children);
    }

    [Theory]
    [InlineData(4096, false)]
    [InlineData(4097, true)]
    public async Task ContextCatalogAcceptsItsExactBoundAndNeverUsesAPartialOverflowCatalog(int count, bool blocked)
    {
        await Import();
        var path = Path.Combine(_directory, "Properties.xsd"); var properties = XDocument.Load(path);
        XNamespace xs = "http://www.w3.org/2001/XMLSchema";
        var template = properties.Root!.Elements(xs + "element").First();
        for (var index = 3; index < count; index++)
        {
            var copy = new XElement(template); copy.SetAttributeValue("name", "P" + index); properties.Root.Add(copy);
        }
        var xml = properties.ToString(SaveOptions.DisableFormatting);
        Assert.True(xml.Length <= 1_000_000);
        await File.WriteAllTextAsync(path, xml);
        var definition = await AnalyzeAgain();
        var node = definition.AllNodes().Single(node => node.Name == "fields");
        Assert.Equal(blocked ? NativeFlowNodeKind.Blocked : NativeFlowNodeKind.Sequence, node.Kind);
        if (blocked) { Assert.Contains("4096", node.BlockReason); Assert.Empty(node.Children); }
    }

    [Theory]
    [InlineData("include")]
    [InlineData("import")]
    [InlineData("redefine")]
    public async Task ContextIdentityNeverResolvesExternalOrRedefinedPropertySchemas(string kind)
    {
        var (_, definition) = await Import(editProperties: properties => properties.Root!.AddFirst(new XElement(XName.Get(kind, "http://www.w3.org/2001/XMLSchema"), new XAttribute("schemaLocation", "https://invalid.example/properties.xsd"))));
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "fields").Kind);
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoEffects : IAugmentModuleInvoker, IRenderModuleInvoker
    {
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No source helper/module should be invoked.");
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No source helper/module should be invoked.");
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
