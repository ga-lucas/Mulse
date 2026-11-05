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

public sealed class NativeXmlCompositionTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-xml-compose-" + Guid.NewGuid().ToString("N"));
    private static NativeExpression Text(string value) => new() { Literal = NativeScalar.From(value) };
    private static NativeFlowNode Create(string name, string xml) => new() { Kind = NativeFlowNodeKind.CreateXmlMessage, MessageName = name, Expression = Text(xml) };
    private static NativeFlowNode Root(string name = "batch", string local = "Batch", string ns = "urn:batch") => new() { Kind = NativeFlowNodeKind.CreateXmlRoot, MessageName = name, Expression = Text(local), NamespaceExpression = Text(ns) };
    private static NativeFlowNode Append(string source = "part", string target = "batch") => new() { Kind = NativeFlowNodeKind.AppendXmlMessage, MessageName = target, SourceMessage = source };
    private static IntegrationBatch Input(string xml = "<input/>") => new([new("input.xml", BinaryData.FromString(xml), "application/xml", new Dictionary<string, string> { ["group"] = "A", ["messageId"] = "one", ["channel"] = "input" })]);
    private static ModuleStepDefinition Step(NativeFlowDefinition definition) => new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };
    private static NativeFlowDefinition Flow(params NativeFlowNode[] nodes) => new() { Durable = true, Nodes = nodes.ToList() };
    private static NativeControlFlowAugmentModule Module(IFlowOrchestrationStateStore store, TimeProvider? clock = null) => new(new NoEffects(), new NoEffects(), store, clock);
    private IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string? first = null, string? next = null, bool declareElement = true, bool scopedElement = false, bool shadowElement = false)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Composition.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName><RootNamespace>Fixture</RootNamespace></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/><Schema Include='Rules.xsd'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Rules.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:b="http://schemas.microsoft.com/BizTalk/2003" targetNamespace="urn:rules">
              <xs:element name="Rules"><xs:annotation><xs:appinfo><b:properties>
                <b:property distinguished="true" xpath="/*[local-name()='Rules' and namespace-uri()='urn:rules']/*[local-name()='RootName' and namespace-uri()='']"/>
                <b:property distinguished="true" xpath="/*[local-name()='Rules' and namespace-uri()='urn:rules']/*[local-name()='RootNamespace' and namespace-uri()='']"/>
              </b:properties></xs:appinfo></xs:annotation><xs:complexType><xs:sequence><xs:element name="RootName" type="xs:string"/><xs:element name="RootNamespace" type="xs:string"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """);
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string type, string name, params object?[] children) => new(ns + "Element", new XAttribute("Type", type), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Compose",
            N("VariableDeclaration", "count", P("Type", "System.Int32")),
            N("VariableDeclaration", "document", P("Type", "System.Xml.XmlDocument"), P("UseDefaultConstructor", "True")),
            declareElement ? N("VariableDeclaration", "element", P("Type", "System.Xml.XmlElement")) : null,
            N("MessageDeclaration", "rules", P("Type", "Fixture.Rules")),
            new[] { "header", "body", "before", "result" }.Select(name => N("MessageDeclaration", name, P("Type", "System.Xml.XmlDocument"))),
            N("ServiceBody", "Body",
                N("Receive", "input", P("MessageName", "rules"), P("PortName", "input")),
                N("Expression", "prepare", P("Expression", "header.LoadXml(\"<h:Header xmlns:h='urn:header'><Id>synthetic</Id></h:Header>\"); body.LoadXml(\"<Record id='first'><![CDATA[a < b]]><!--inside--><?note ok?></Record>\");")),
                N("Expression", "start", P("Expression", first ?? "document = new System.Xml.XmlDocument(); element = document.CreateElement(\"\", rules.RootName, rules.RootNamespace); document.AppendChild(element); document.DocumentElement.AppendChild(document.ImportNode(header.FirstChild, true)); document.DocumentElement.AppendChild(document.ImportNode(body.FirstChild, true)); count = 0; count = count + 1; before = document;")),
                N("Expression", "next", P("Expression", next ?? "body.LoadXml(\"<Record id='second'/>\"); document.DocumentElement.AppendChild(document.ImportNode(body.FirstChild, true)); count = count + 1;")),
                N("Construct", "output", N("MessageAssignment", "copy", P("Expression", "result = document;")), N("MessageRef", "result-ref", P("Ref", "result"))),
                N("Send", "reply", P("MessageName", "result"), P("OperationMessageName", "Response")))));
        if (scopedElement)
        {
            var declaration = source.Descendants(ns + "Element").Single(node => (string?)node.Attribute("Type") == "VariableDeclaration" && node.Elements(ns + "Property").Any(property => (string?)property.Attribute("Name") == "Name" && (string?)property.Attribute("Value") == "element"));
            var start = source.Descendants(ns + "Element").Single(node => node.Elements(ns + "Property").Any(property => (string?)property.Attribute("Name") == "Name" && (string?)property.Attribute("Value") == "start"));
            declaration.Remove();
            var scope = N("Scope", "scope"); start.ReplaceWith(scope); scope.Add(declaration, start);
            if (shadowElement) source.Elements(ns + "Element").Single().Add(N("VariableDeclaration", "element", P("Type", "System.Xml.XmlElement")));
        }
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new() { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    [Fact]
    public async Task ImportedBatchRootHeaderAndRecordAccumulationSurviveDiskRestartWithIndependentCopies()
    {
        var (candidate, definition) = await Import();
        Assert.False(candidate.DraftFlow.Enabled);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.CreateXmlRoot);
        Assert.Equal(3, definition.AllNodes().Count(node => node.Kind == NativeFlowNodeKind.AppendXmlMessage));
        var body = definition.AllNodes().Single(node => node.Name == "Body");
        body.Children.Insert(3, new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5 });
        var clock = new Clock(); var first = new FlowExecutionContext("compose", "first", clock.Now);
        const string input = "<r:Rules xmlns:r='urn:rules'><RootName>Envelope</RootName><RootNamespace>urn:configured</RootNamespace></r:Rules>";
        await Module(Store(), clock).AugmentAsync(first, Input(input), Step(definition), default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        var saved = Assert.Single(await Store().GetAllAsync("compose", default));
        Assert.Equal("1", saved.Native!.State.Variables["count"].Value);
        Assert.Equal(2, XDocument.Parse(saved.Native.State.Messages["document"].Single().Restore().GetText()).Root!.Elements().Count());
        clock.Now = clock.Now.AddSeconds(5);
        var resumed = new FlowExecutionContext("compose", "resumed", clock.Now);
        resumed.SetOrchestrationInstance(saved.InstanceId, saved.CorrelationKey, saved.Bookmark, true);
        await Module(Store(), clock).AugmentAsync(resumed, IntegrationBatch.Empty, Step(definition), default);
        var response = Assert.Single(resumed.CapturedResponses);
        var output = XDocument.Parse(response.GetText());
        Assert.Equal(XName.Get("Envelope", "urn:configured"), output.Root!.Name);
        Assert.Equal(new[] { XName.Get("Header", "urn:header"), XName.Get("Record"), XName.Get("Record") }, output.Root.Elements().Select(element => element.Name));
        Assert.Equal(new[] { "first", "second" }, output.Root.Elements("Record").Select(element => (string?)element.Attribute("id")));
        Assert.IsType<XCData>(output.Root.Element("Record")!.FirstNode);
        Assert.Contains(output.Root.Element("Record")!.Nodes(), node => node is XComment);
        Assert.Contains(output.Root.Element("Record")!.Nodes(), node => node is XProcessingInstruction);
        Assert.Empty(response.Metadata);
        saved = Assert.Single(await Store().GetAllAsync("compose", default));
        Assert.Equal("2", saved.Native!.State.Variables["count"].Value);
        Assert.Equal(2, XDocument.Parse(saved.Native.State.Messages["before"].Single().Restore().GetText()).Root!.Elements().Count());
        Assert.Equal(input, saved.Native.State.Messages["rules"].Single().Restore().GetText());
        Assert.Equal("Completed", saved.Native.State.Status);
        var html = await Render(candidate.DraftFlow.Augments.Single().Settings["flowJson"]);
        Assert.Contains("Root namespace expression", html); Assert.Contains("Source XML message", html);
        Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete", html);
    }

    [Theory]
    [InlineData("document = new System.Xml.XmlDocument(); element = document.CreateElement(\"p\", \"Root\", \"urn:r\"); document.AppendChild(element);")]
    [InlineData("document = new System.Xml.XmlDocument(); element = document.CreateElement(\"\", \"Root\", \"urn:r\"); header.AppendChild(element);")]
    [InlineData("document = new System.Xml.XmlDocument(); element = document.CreateElement(\"\", \"Root\", \"urn:r\"); document.AppendChild(header);")]
    [InlineData("document = new System.Xml.XmlDocument(); element = document.CreateElement(\"\", xpath(document, \"string(/r)\"), \"urn:r\"); document.AppendChild(element);")]
    [InlineData("document = new System.Xml.XmlDocument(); element = document.CreateElement(\"\", \"Root\", \"urn:r\"); document.AppendChild(element); Unknown.Run();")]
    [InlineData("document.DocumentElement.AppendChild(document.ImportNode(body.FirstChild, false));")]
    [InlineData("document.DocumentElement.AppendChild(header.ImportNode(body.FirstChild, true));")]
    [InlineData("document.DocumentElement.AppendChild(document.ImportNode(unknown.FirstChild, true));")]
    [InlineData("document.DocumentElement.AppendChild(document.ImportNode(body.DocumentElement, true));")]
    [InlineData("element = document.CreateElement(\"\", \"Root\", \"urn:r\"); document.AppendChild(element);")]
    public async Task UnsupportedDomPatternsRemainWholeStatementBlockers(string expression)
    {
        var (_, definition) = await Import(first: expression);
        var blocked = definition.AllNodes().Single(node => node.Name == "start");
        Assert.Equal(NativeFlowNodeKind.Blocked, blocked.Kind);
        Assert.Empty(blocked.Children);
    }

    [Fact]
    public async Task UndeclaredTemporaryElementCannotBeInferredByName()
    {
        var (_, definition) = await Import(declareElement: false);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "start").Kind);
    }

    [Theory]
    [InlineData("<?xml version='1.0'?><part/>")]
    [InlineData("<!--leading--><part/>")]
    [InlineData("<?leading value?><part/>")]
    [InlineData(" <part/>")]
    public async Task NativeAppendNeverSubstitutesRootForADifferentFirstChild(string source)
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var flow = Flow(new() { Kind = NativeFlowNodeKind.Receive, MessageName = "request", Channel = "input" }, Root(), Create("part", source), Append());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Module(store).AugmentAsync(new("compose", "run", DateTimeOffset.UtcNow), Input(), Step(flow), default));
        var saved = Assert.Single(await store.GetAllAsync("compose", default));
        Assert.Equal("Faulted", saved.Native!.State.Status);
        Assert.Empty(XDocument.Parse(saved.Native.State.Messages["batch"].Single().Restore().GetText()).Root!.Elements());
        var persisted = saved.Native.State.Messages["part"].Single().Restore().GetText();
        if (source.StartsWith("<?xml", StringComparison.Ordinal)) Assert.NotNull(XDocument.Parse(persisted).Declaration);
        else Assert.Equal(source, persisted);
        Assert.Null(saved.Native.State.EffectNodeId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("p:Root")]
    [InlineData("<Root/>")]
    [InlineData("two names")]
    public async Task InvalidRootNamesFailBeforeReplacingExistingSnapshot(string name)
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var flow = Flow(new() { Kind = NativeFlowNodeKind.Receive, MessageName = "request", Channel = "input" }, Create("batch", "<original/>"), Root(local: name));
        await Assert.ThrowsAsync<ArgumentException>(() => Module(store).AugmentAsync(new("compose", "run", DateTimeOffset.UtcNow), Input(), Step(flow), default));
        Assert.Equal("<original/>", Assert.Single(await store.GetAllAsync("compose", default)).Native!.State.Messages["batch"].Single().Restore().GetText());
    }

    [Fact]
    public async Task LocalCompositionBeforeUnmatchedReceiveCreatesNoCheckpointAndDoesNotConsumeArrival()
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var flow = Flow(Root(), Create("part", "<part/>"), Append(), new() { Kind = NativeFlowNodeKind.Receive, MessageName = "request", Channel = "other" });
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => Module(store).AugmentAsync(new("compose", "run", DateTimeOffset.UtcNow), Input(), Step(flow), default));
        Assert.Empty(await store.GetAllAsync("compose", default));
    }

    [Fact]
    public void CompositionDefinitionsRequireActiveDurableBoundedFieldsOnly()
    {
        var flow = Flow(Root(), Append()); NativeFlowDefinition.Read(Step(flow).Settings["flowJson"]);
        flow.Durable = false; Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Step(flow).Settings["flowJson"])); flow.Durable = true;
        flow.Nodes[0].NamespaceExpression = null; Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Step(flow).Settings["flowJson"])); flow.Nodes[0].NamespaceExpression = Text("");
        flow.Nodes[1].SourceMessage = " "; Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Step(flow).Settings["flowJson"])); flow.Nodes[1].SourceMessage = "part";
        flow.Nodes[1].Expression = Text("inactive"); Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Step(flow).Settings["flowJson"])); flow.Nodes[1].Expression = null;
        flow.Nodes[1].NamespaceExpression = Text("inactive"); Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Step(flow).Settings["flowJson"])); flow.Nodes[1].NamespaceExpression = null;
        flow.Nodes[0].SourceMessage = "inactive"; Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(Step(flow).Settings["flowJson"]));
    }

    [Fact]
    public async Task CompositionCanBeAuthoredFromScratchWithGraphicalCallbacks()
    {
        var model = new NativeFlowEditorModel { Durable = true };
        var editor = new NativeFlowNodesEditor(); Set(editor, nameof(editor.Nodes), model.Nodes);
        Set(editor, nameof(editor.MessageNames), new[] { "batch", "part" });
        string saved = "";
        Set(editor, nameof(editor.Changed), EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(editor, "Add"); var root = model.Nodes.Single(); await Invoke(editor, "ChangeKind", root, "CreateXmlRoot");
        root.MessageName = "batch"; root.Expression!.Literal!.Value = "Envelope"; root.NamespaceExpression!.Literal!.Value = "urn:scratch";
        await Invoke(editor, "Add"); var append = model.Nodes.Last(); await Invoke(editor, "ChangeKind", append, "AppendXmlMessage");
        append.MessageName = "batch"; append.SourceMessage = "part"; await Invoke(editor, "Notify");
        var flow = NativeFlowDefinition.Read(saved);
        Assert.Equal("urn:scratch", flow.Nodes[0].NamespaceExpression!.Literal!.Value);
        Assert.Equal("part", flow.Nodes[1].SourceMessage);
        var html = await Render(saved);
        Assert.Contains("Root local-name expression", html); Assert.Contains("Root namespace expression", html); Assert.Contains("Source XML message", html);
        Assert.DoesNotContain("Some nodes are incomplete", html); Assert.DoesNotContain("Original JSON is preserved", html);
        await Invoke(editor, "ChangeKind", root, "Sequence"); await Invoke(editor, "ChangeKind", append, "Sequence");
        flow = NativeFlowDefinition.Read(saved);
        Assert.All(flow.Nodes, node => { Assert.Null(node.NamespaceExpression); Assert.Null(node.SourceMessage); });
    }

    [Fact]
    public async Task ScopedTemporaryRootIsRecognizedOnlyInItsOwningScopeAndDoesNotUnblockTransactionSemantics()
    {
        const string expression = "document = new System.Xml.XmlDocument(); element = document.CreateElement(\"\", rules.RootName, rules.RootNamespace); document.AppendChild(element);";
        var (_, definition) = await Import(first: expression, next: expression, scopedElement: true);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "scope").Kind);
        Assert.Equal(NativeFlowNodeKind.Sequence, definition.AllNodes().Single(node => node.Name == "start").Kind);
        Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.CreateXmlRoot);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "next").Kind);
        (_, definition) = await Import(first: expression, scopedElement: true, shadowElement: true);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "start").Kind);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.CreateXmlRoot);
    }

    [Theory]
    [InlineData("Characters", false)]
    [InlineData("Characters", true)]
    [InlineData("Nodes", false)]
    [InlineData("Nodes", true)]
    [InlineData("Depth", false)]
    [InlineData("Depth", true)]
    public async Task AppendedXmlEnforcesExactBoundsBeforeReplacingNamedTarget(string limit, bool exceed)
    {
        var target = limit switch
        {
            "Characters" => "<Batch>" + new string('x', 500000 - 15) + "</Batch>",
            "Nodes" => "<Batch>" + string.Concat(Enumerable.Repeat("<Item/>", 24998)) + "</Batch>",
            _ => "<Batch/>"
        };
        var source = limit switch
        {
            "Characters" => "<Part>" + new string('x', 500000 - 13 + (exceed ? 1 : 0)) + "</Part>",
            "Nodes" => "<Part>" + string.Concat(Enumerable.Repeat("<Item/>", 24998 + (exceed ? 1 : 0))) + "</Part>",
            _ => string.Concat(Enumerable.Repeat("<r>", 64 + (exceed ? 1 : 0))) + string.Concat(Enumerable.Repeat("</r>", 64 + (exceed ? 1 : 0)))
        };
        var flow = Flow(new() { Kind = NativeFlowNodeKind.Receive, MessageName = "batch", Channel = "input" }, new() { Kind = NativeFlowNodeKind.Receive, MessageName = "part", Channel = "input" }, Append(), new() { Kind = NativeFlowNodeKind.Reply, MessageName = "batch" });
        var store = new InMemoryFlowOrchestrationStateStore();
        var first = new FlowExecutionContext("compose", "first", DateTimeOffset.UtcNow);
        await Module(store).AugmentAsync(first, Input(target), Step(flow), default);
        var saved = Assert.Single(await store.GetAllAsync("compose", default));
        var resumed = new FlowExecutionContext("compose", "second", DateTimeOffset.UtcNow);
        resumed.SetOrchestrationInstance(saved.InstanceId, saved.CorrelationKey, saved.Bookmark, true);
        var payload = Input(source).Payloads.Single();
        var incoming = new IntegrationBatch([payload with { Metadata = new Dictionary<string, string>(payload.Metadata) { ["messageId"] = "two" } }]);
        if (exceed)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => Module(store).AugmentAsync(resumed, incoming, Step(flow), default));
            saved = Assert.Single(await store.GetAllAsync("compose", default));
            Assert.Equal("Faulted", saved.Native!.State.Status);
            Assert.Equal(target, saved.Native.State.Messages["batch"].Single().Restore().GetText());
            Assert.Empty(resumed.CapturedResponses);
        }
        else
        {
            await Module(store).AugmentAsync(resumed, incoming, Step(flow), default);
            var response = Assert.Single(resumed.CapturedResponses);
            Assert.Equal("one", response.Metadata["messageId"]);
            Assert.Equal("input.xml", response.Name); Assert.Equal("application/xml", response.ContentType);
            if (limit == "Characters") Assert.Equal(1_000_000, response.GetText().Length);
            if (limit == "Nodes") Assert.Equal(49998, XDocument.Parse(response.GetText()).Descendants().Count());
            if (limit == "Depth") Assert.Equal(65, XDocument.Parse(response.GetText()).Descendants().Count());
        }
        saved = Assert.Single(await store.GetAllAsync("compose", default));
        Assert.Equal(source, saved.Native!.State.Messages["part"].Single().Restore().GetText());
    }

    [Theory]
    [InlineData(256, 2048, false)]
    [InlineData(257, 2048, true)]
    [InlineData(256, 2049, true)]
    public async Task RootNameAndNamespaceLengthsHaveExplicitBounds(int nameLength, int namespaceLength, bool invalid)
    {
        var flow = Flow(Root(local: new string('r', nameLength), ns: "urn:" + new string('n', namespaceLength - 4)));
        var store = new InMemoryFlowOrchestrationStateStore();
        var context = new FlowExecutionContext("compose", "run", DateTimeOffset.UtcNow);
        if (invalid) await Assert.ThrowsAsync<ArgumentException>(() => Module(store).AugmentAsync(context, Input(), Step(flow), default));
        else
        {
            await Module(store).AugmentAsync(context, Input(), Step(flow), default);
            var saved = Assert.Single(await store.GetAllAsync("compose", default));
            var root = XDocument.Parse(saved.Native!.State.Messages["batch"].Single().Restore().GetText()).Root!;
            Assert.Equal(nameLength, root.Name.LocalName.Length); Assert.Equal(namespaceLength, root.Name.NamespaceName.Length);
        }
    }

    [Theory]
    [InlineData("http://www.w3.org/2000/xmlns/")]
    [InlineData("http://www.w3.org/XML/1998/namespace")]
    public async Task ReservedRootNamespacesFailExplicitly(string ns)
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        await Assert.ThrowsAsync<ArgumentException>(() => Module(store).AugmentAsync(new("compose", "run", DateTimeOffset.UtcNow), Input(), Step(Flow(Root(ns: ns))), default));
        Assert.Empty(await store.GetAllAsync("compose", default));
    }

    [Fact]
    public async Task NonStringRootExpressionAndUninitializedAppendSourceNeverDefaultOrCoerce()
    {
        var store = new InMemoryFlowOrchestrationStateStore(); var root = Root(); root.Expression = new() { Literal = NativeScalar.From(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Module(store).AugmentAsync(new("compose", "run", DateTimeOffset.UtcNow), Input(), Step(Flow(root)), default));
        Assert.Empty(await store.GetAllAsync("compose", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Module(store).AugmentAsync(new("compose", "run", DateTimeOffset.UtcNow), Input(), Step(Flow(Root(), Append())), default));
        Assert.Empty(await store.GetAllAsync("compose", default));
    }

    [Fact]
    public async Task SelfAppendCopiesTheExistingRootWithoutAliasingOrRecursing()
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var flow = Flow(Create("batch", "<Batch><Item/></Batch>"), Append("batch"), new() { Kind = NativeFlowNodeKind.Reply, MessageName = "batch" });
        var context = new FlowExecutionContext("compose", "run", DateTimeOffset.UtcNow);
        await Module(store).AugmentAsync(context, Input(), Step(flow), default);
        var output = XDocument.Parse(Assert.Single(context.CapturedResponses).GetText());
        Assert.Equal(4, output.Descendants().Count());
        Assert.Single(output.Root!.Elements("Batch")); Assert.Single(output.Root.Element("Batch")!.Elements("Item"));
    }

    [Fact]
    public async Task XmlDeclarationSurvivesConstructionTypedWriteAndAppendWithCorrectUtf8Encoding()
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var flow = Flow(Create("batch", "<?xml version='1.0' encoding='utf-16' standalone='yes'?><Batch><Value>old</Value></Batch>"),
            new() { Kind = NativeFlowNodeKind.SetMessageValue, Target = new() { Kind = NativeMessageFactKind.XmlString, MessageName = "batch", Steps = [new() { Name = "Batch" }, new() { Name = "Value" }] }, Expression = Text("updated") },
            Create("part", "<Part/>"), Append(), new() { Kind = NativeFlowNodeKind.Reply, MessageName = "batch" });
        var context = new FlowExecutionContext("compose", "run", DateTimeOffset.UtcNow);
        await Module(store).AugmentAsync(context, Input(), Step(flow), default);
        var payload = Assert.Single(context.CapturedResponses);
        using var stream = payload.Content.ToStream();
        var output = XDocument.Load(stream);
        Assert.Equal("utf-8", output.Declaration!.Encoding); Assert.Equal("yes", output.Declaration.Standalone);
        Assert.Equal("updated", output.Root!.Element("Value")!.Value); Assert.NotNull(output.Root.Element("Part"));
    }

    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static async Task Invoke(object target, string method, params object?[] arguments) => await (Task)target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, arguments)!;
    private static async Task<string> Render(string json)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = json }))).ToHtmlString());
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoEffects : IAugmentModuleInvoker, IRenderModuleInvoker
    {
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("Composition must not call source helpers or external modules.");
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("Composition must not call source helpers or external modules.");
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
