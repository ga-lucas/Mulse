using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Ui.Components.Shared;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeSchemaMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-schema-migration-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };
    private static IntegrationBatch Input(string body) => new([new("request.xml", BinaryData.FromString(body), "application/xml", new Dictionary<string, string>
        { ["group"] = "A", ["messageId"] = "1", ["channel"] = "input" })]);
    private static NativeMessageFact Fact(NativeMessageFactKind kind) => new() { Kind = kind, Steps = [new() { Name = "r", Namespace = "urn:r" }, new() { Name = "value" }] };

    [Theory]
    [InlineData(NativeMessageFactKind.XmlString, "  x  ", NativeScalarType.String, "  x  ")]
    [InlineData(NativeMessageFactKind.XmlInt32, " +0042 ", NativeScalarType.Int32, "42")]
    [InlineData(NativeMessageFactKind.XmlInt32, "-2147483648", NativeScalarType.Int32, "-2147483648")]
    [InlineData(NativeMessageFactKind.XmlBoolean, "1", NativeScalarType.Boolean, "true")]
    [InlineData(NativeMessageFactKind.XmlBoolean, " false ", NativeScalarType.Boolean, "false")]
    [InlineData(NativeMessageFactKind.XmlBoolean, "0", NativeScalarType.Boolean, "false")]
    public void StrictXmlScalarsPreserveXsdLexicalAndNativeTypeSemantics(NativeMessageFactKind kind, string text, NativeScalarType expectedType, string expected)
    {
        var result = Fact(kind).Read(Input("<p:r xmlns:p='urn:r'><value>" + text + "</value></p:r>"));
        Assert.Equal(expectedType, result.Type); Assert.Equal(expected, result.Value);
    }

    [Theory]
    [InlineData("<p:r xmlns:p='urn:r'/>")]
    [InlineData("<p:r xmlns:p='urn:r'><value>x</value><value>y</value></p:r>")]
    [InlineData("<p:r xmlns:p='urn:r'><value><complex>x</complex></value></p:r>")]
    [InlineData("<r xmlns='urn:r'><value>x</value></r>")]
    [InlineData("<p:r xmlns:p='urn:r'><value xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:nil='true'/></p:r>")]
    public void StrictXmlScalarsDoNotSilentlyDefaultMissingAmbiguousComplexOrNilValues(string xml)
        => Assert.Throws<InvalidOperationException>(() => Fact(NativeMessageFactKind.XmlString).Read(Input(xml)));

    [Theory]
    [InlineData(NativeMessageFactKind.XmlInt32, "2147483648")]
    [InlineData(NativeMessageFactKind.XmlInt32, "1.0")]
    [InlineData(NativeMessageFactKind.XmlBoolean, "TRUE")]
    [InlineData(NativeMessageFactKind.XmlBoolean, "yes")]
    public void InvalidXmlLexicalValuesFail(NativeMessageFactKind kind, string text)
        => Assert.Throws<ArgumentException>(() => Fact(kind).Read(Input("<p:r xmlns:p='urn:r'><value>" + text + "</value></p:r>")));

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string expression = "chosen = request.Config.Target; count = request.Config.Count; flag = request.Config.Enabled;", bool duplicateSchema = false, bool undeclaredImport = false, bool reply = false, Action<XDocument, XDocument>? editSchemas = null)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Fixture.btproj");
        await File.WriteAllTextAsync(project, $"<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName><RootNamespace>Fixture</RootNamespace></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/><Schema Include='Message.xsd'/>{(undeclaredImport ? "" : "<Schema Include='Shared.xsd'/>")}{(duplicateSchema ? "<Schema Include='Duplicate.xsd'><Namespace>Fixture</Namespace><TypeName>Message</TypeName></Schema>" : "")}</ItemGroup></Project>");
        const string schema = """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:b="http://schemas.microsoft.com/BizTalk/2003" xmlns:s="urn:shared" targetNamespace="urn:request">
              <xs:import namespace="urn:shared" schemaLocation="Shared.xsd"/>
              <xs:element name="Request"><xs:annotation><xs:appinfo><b:properties>
                <b:property distinguished="true" xpath="/*[local-name()='Request' and namespace-uri()='urn:request']/*[local-name()='Config' and namespace-uri()='']/*[local-name()='Target' and namespace-uri()='']"/>
                <b:property distinguished="true" xpath="/*[local-name()='Request' and namespace-uri()='urn:request']/*[local-name()='Config' and namespace-uri()='']/*[local-name()='Count' and namespace-uri()='']"/>
                <b:property distinguished="true" xpath="/*[local-name()='Request' and namespace-uri()='urn:request']/*[local-name()='Config' and namespace-uri()='']/*[local-name()='Enabled' and namespace-uri()='']"/>
              </b:properties></xs:appinfo></xs:annotation><xs:complexType><xs:sequence><xs:element name="Config" type="s:Configuration"/></xs:sequence></xs:complexType></xs:element>
            </xs:schema>
            """;
        await File.WriteAllTextAsync(Path.Combine(_directory, "Message.xsd"), schema);
        if (duplicateSchema) await File.WriteAllTextAsync(Path.Combine(_directory, "Duplicate.xsd"), schema);
        await File.WriteAllTextAsync(Path.Combine(_directory, "Shared.xsd"), """
            <xs:schema xmlns:xs="http://www.w3.org/2001/XMLSchema" targetNamespace="urn:shared">
              <xs:complexType name="Configuration"><xs:sequence><xs:element name="Target" type="xs:string"/><xs:element name="Count" type="xs:int"/><xs:element name="Enabled" type="xs:boolean"/></xs:sequence></xs:complexType>
            </xs:schema>
            """);
        if (editSchemas is not null)
        {
            var message = XDocument.Parse(schema);
            var shared = XDocument.Load(Path.Combine(_directory, "Shared.xsd"));
            editSchemas(message, shared);
            await File.WriteAllTextAsync(Path.Combine(_directory, "Message.xsd"), message.ToString());
            await File.WriteAllTextAsync(Path.Combine(_directory, "Shared.xsd"), shared.ToString());
        }
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Flow",
            N("VariableDeclaration", "chosen", P("Type", "System.String")), N("VariableDeclaration", "count", P("Type", "System.Int32")), N("VariableDeclaration", "flag", P("Type", "System.Boolean")),
            N("VariableDeclaration", "xml", P("Type", "System.Xml.XmlDocument"), P("UseDefaultConstructor", "True")),
            N("MessageDeclaration", "request", P("Type", "Fixture.Message")), N("MessageDeclaration", "output", P("Type", "Fixture.Message")),
            N("ServiceBody", "Body", N("Receive", "input", P("MessageName", "request"), P("PortName", "input")),
                N("Construct", "construct", N("MessageAssignment", "fields", P("Expression", expression)), N("MessageRef", "constructed-message", P("Ref", "output"))),
                N("Decision", "branch", N("DecisionBranch", "enabled", P("Expression", "request.Config.Enabled && request.Config.Count == 42")), N("DecisionBranch", "otherwise")),
                reply ? N("Send", "reply", P("MessageName", "output"), P("OperationMessageName", "Response")) : N("Construct", "end"))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new() { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    [Fact]
    public async Task AutomaticImportResolvesDeclaredImportedXsdTypesAndExecutesNativeMembersWithoutSourceRuntime()
    {
        var (candidate, definition) = await Import();
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Equal(NativeScalarType.Null, definition.AllNodes().Single(node => node.Variable == "chosen" && node.Kind == NativeFlowNodeKind.DeclareVariable).Expression!.Literal!.Type);
        Assert.Equal("0", definition.AllNodes().Single(node => node.Variable == "count" && node.Kind == NativeFlowNodeKind.DeclareVariable).Expression!.Literal!.Value);
        Assert.Equal("false", definition.AllNodes().Single(node => node.Variable == "flag" && node.Kind == NativeFlowNodeKind.DeclareVariable).Expression!.Literal!.Value);
        var json = candidate.DraftFlow.Augments.Single().Settings["flowJson"];
        Assert.DoesNotContain("request.Config.Target", json);
        var store = new InMemoryFlowOrchestrationStateStore();
        var module = new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store);
        await module.AugmentAsync(new("schema", "run", DateTimeOffset.UtcNow), Input("<r:Request xmlns:r='urn:request'><Config><Target>file.xml</Target><Count>42</Count><Enabled>1</Enabled></Config></r:Request>"), new()
            { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = json } }, default);
        var checkpoint = Assert.Single(await store.GetAllAsync("schema", default));
        Assert.Equal("file.xml", checkpoint.Native!.State.Variables["chosen"].Value);
        Assert.Equal("42", checkpoint.Native.State.Variables["count"].Value);
        Assert.Equal("true", checkpoint.Native.State.Variables["flag"].Value);
        Assert.False(candidate.DraftFlow.Enabled);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = json }))).ToHtmlString());
        Assert.Contains("XmlBoolean", html); Assert.Contains("XmlInt32", html); Assert.Contains("urn:request", html);
        Assert.DoesNotContain("Original JSON is preserved", html);
    }

    [Theory]
    [InlineData(true, false, "chosen = request.Config.Target;")]
    [InlineData(false, true, "chosen = request.Config.Target;")]
    [InlineData(false, false, "chosen = request.Config.target;")]
    [InlineData(false, false, "chosen = request.Config.Target; Arbitrary.Run();")]
    public async Task AmbiguousUndeclaredOrUnsupportedMembersRemainWholeStatementBlockers(bool duplicate, bool undeclared, string expression)
    {
        var (_, definition) = await Import(expression, duplicate, undeclared);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "fields").Kind);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.SetVariable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportedXmlConstructionCopiesAndTypedWritesSurviveDiskRestartWithoutChangingSource(bool explicitConstructor)
    {
        const string xml = "<p:Request xmlns:p='urn:request'><Config><Target>initial</Target><Count>0</Count><Enabled>false</Enabled></Config></p:Request>";
        var expression = (explicitConstructor ? "xml = new System.Xml.XmlDocument(); " : "") + "xml.LoadXml(\"" + xml + "\"); output = xml; " +
            "output.Config.Target = request.Config.Target + \" & <escaped>\"; output.Config.Count = request.Config.Count + 1; output.Config.Enabled = !request.Config.Enabled;";
        var (candidate, definition) = await Import(expression, reply: true);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.CreateXmlMessage);
        Assert.Equal(3, definition.AllNodes().Count(node => node.Kind == NativeFlowNodeKind.SetMessageValue));
        var fields = definition.AllNodes().Single(node => node.Name == "fields");
        fields.Children.Insert(3, new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5 });
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };
        IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Microsoft.Extensions.Options.Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
        var clock = new Clock();
        var original = Input("<r:Request xmlns:r='urn:request'><Config><Target>file.xml</Target><Count>42</Count><Enabled>1</Enabled></Config></r:Request>");
        var first = new FlowExecutionContext("schema", "first", clock.Now);
        await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(first, original, step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        var checkpoint = Assert.Single(await Store().GetAllAsync("schema", default));
        Assert.Equal("initial", XDocument.Parse(checkpoint.Native!.State.Messages["output"].Single().Restore().GetText()).Root!.Element("Config")!.Element("Target")!.Value);
        clock.Now = clock.Now.AddSeconds(5);
        var resumed = new FlowExecutionContext("schema", "second", clock.Now);
        resumed.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, checkpoint.Bookmark, true);
        await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(resumed, IntegrationBatch.Empty, step, default);
        var reply = XDocument.Parse(Assert.Single(resumed.CapturedResponses).GetText());
        Assert.Equal("file.xml & <escaped>", reply.Root!.Element("Config")!.Element("Target")!.Value);
        Assert.Equal("43", reply.Root.Element("Config")!.Element("Count")!.Value);
        Assert.Equal("false", reply.Root.Element("Config")!.Element("Enabled")!.Value);
        checkpoint = Assert.Single(await Store().GetAllAsync("schema", default));
        Assert.Equal(original.Payloads[0].GetText(), checkpoint.Native!.State.Messages["request"].Single().Restore().GetText());
        Assert.Equal("initial", XDocument.Parse(checkpoint.Native.State.Messages["xml"].Single().Restore().GetText()).Root!.Element("Config")!.Element("Target")!.Value);
        Assert.Empty(checkpoint.Native.State.Messages["output"].Single().Metadata);
        Assert.Equal("Completed", checkpoint.Native.State.Status);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = candidate.DraftFlow.Augments.Single().Settings["flowJson"] }))).ToHtmlString());
        Assert.Contains("Write target", html); Assert.Contains("CreateXmlMessage", html); Assert.DoesNotContain("Original JSON is preserved", html);
    }

    [Theory]
    [InlineData("xml = new System.Xml.XmlDocument();")]
    [InlineData("xml = new System.Xml.XmlDocument(); output = xml;")]
    [InlineData("xml = new System.Xml.XmlDocument(); xml.LoadXml(xpath(xml, \"string(/r)\"));")]
    [InlineData("xml.Load(\"external.xml\");")]
    [InlineData("xml.LoadXml(\"<r/>\"); Unknown.Run();")]
    [InlineData("request.Config.Target = \"x\"; Unknown.Run();")]
    public async Task UnsupportedConstructionOrWriteSuffixNeverProducesPartialNativeOperations(string expression)
    {
        var (_, definition) = await Import(expression);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "fields").Kind);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind is NativeFlowNodeKind.CreateXmlMessage or NativeFlowNodeKind.SetMessageValue);
    }

    [Fact]
    public async Task NativeConstructionBeforeInitialReceiveDoesNotAcceptUnmatchedArrival()
    {
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [
            new() { Kind = NativeFlowNodeKind.CreateXmlMessage, MessageName = "local", Expression = new() { Literal = NativeScalar.From("<local/>") } },
            new() { Kind = NativeFlowNodeKind.UseMessage, MessageName = "local" },
            new() { Kind = NativeFlowNodeKind.SaveMessage, MessageName = "copy" },
            new() { Kind = NativeFlowNodeKind.Receive, Channel = "expected", MessageName = "request" }
        ] };
        var store = new InMemoryFlowOrchestrationStateStore();
        await Assert.ThrowsAsync<NativeUnmatchedMessageException>(() => new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store).AugmentAsync(new("schema", "first", DateTimeOffset.UtcNow), Input("<r/>"),
            new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } }, default));
        Assert.Empty(await store.GetAllAsync("schema", default));
    }

    [Fact]
    public void NativeWriteDefinitionRejectsInactiveTargetsAndNondurableOrUntypedWrites()
    {
        var target = Fact(NativeMessageFactKind.XmlString); target.MessageName = "named";
        var node = new NativeFlowNode { Kind = NativeFlowNodeKind.SetMessageValue, Target = target, Expression = new() { Literal = NativeScalar.From("value") } };
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [node] };
        NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json));
        definition.Durable = false;
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json)));
        definition.Durable = true; target.Kind = NativeMessageFactKind.XmlText;
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json)));
        target.Kind = NativeMessageFactKind.XmlString; node.Kind = NativeFlowNodeKind.Sequence; node.Expression = null;
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json)));
    }

    [Fact]
    public async Task UnsupportedDistinguishedFieldDoesNotPoisonOtherProvenFieldsButRemainsBlockedWhenUsed()
    {
        void AddUnsupported(XDocument message, XDocument shared)
        {
            XNamespace xs = "http://www.w3.org/2001/XMLSchema", b = "http://schemas.microsoft.com/BizTalk/2003";
            var property = new XElement(message.Descendants(b + "property").First());
            property.SetAttributeValue("xpath", property.Attribute("xpath")!.Value.Replace("Target", "When", StringComparison.Ordinal));
            message.Descendants(b + "properties").Single().Add(property);
            shared.Descendants(xs + "sequence").Single().Add(new XElement(xs + "element", new XAttribute("name", "When"), new XAttribute("type", "xs:dateTime")));
        }
        var (_, supported) = await Import(editSchemas: AddUnsupported);
        Assert.DoesNotContain(supported.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        var (_, unsupported) = await Import("chosen = request.Config.When;", editSchemas: AddUnsupported);
        var blocker = unsupported.AllNodes().Single(node => node.Name == "fields");
        Assert.Equal(NativeFlowNodeKind.Blocked, blocker.Kind);
        Assert.Contains("datatype", blocker.BlockReason);
    }

    [Fact]
    public async Task DuplicateDistinguishedIdentityIsNeverChosenEvenWhenOtherFieldsAreValid()
    {
        var (_, definition) = await Import(editSchemas: (message, _) =>
        {
            XNamespace b = "http://schemas.microsoft.com/BizTalk/2003";
            message.Descendants(b + "properties").Single().Add(new XElement(message.Descendants(b + "property").First()));
        });
        var blocker = definition.AllNodes().Single(node => node.Name == "fields");
        Assert.Equal(NativeFlowNodeKind.Blocked, blocker.Kind);
        Assert.Contains("ambiguous", blocker.BlockReason);
        Assert.DoesNotContain(blocker.Children, node => node.Kind == NativeFlowNodeKind.SetVariable);
        Assert.Equal(NativeFlowNodeKind.If, definition.AllNodes().Single(node => node.Name == "enabled").Kind);
    }

    [Theory]
    [InlineData("<p:r xmlns:p='urn:r'/>", NativeMessageFactKind.XmlString, NativeScalarType.String)]
    [InlineData("<p:r xmlns:p='urn:r'><value>a</value><value>b</value></p:r>", NativeMessageFactKind.XmlString, NativeScalarType.String)]
    [InlineData("<p:r xmlns:p='urn:r'><value><complex/></value></p:r>", NativeMessageFactKind.XmlString, NativeScalarType.String)]
    [InlineData("<p:r xmlns:p='urn:r'><value xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:nil='true'/></p:r>", NativeMessageFactKind.XmlString, NativeScalarType.String)]
    [InlineData("<p:r xmlns:p='urn:r'><value>1</value></p:r>", NativeMessageFactKind.XmlInt32, NativeScalarType.String)]
    [InlineData("<p:r xmlns:p='urn:r'><value>true</value></p:r>", NativeMessageFactKind.XmlBoolean, NativeScalarType.String)]
    [InlineData("<p:r xmlns:p='urn:r'><value>x</value></p:r>", NativeMessageFactKind.XmlString, NativeScalarType.Null)]
    public async Task InvalidNativeWritesFaultWithoutChangingNamedOrWorkingMessage(string xml, NativeMessageFactKind kind, NativeScalarType valueType)
    {
        var target = Fact(kind); target.MessageName = "request";
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [
            new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "request" },
            new() { Kind = NativeFlowNodeKind.SetMessageValue, Target = target, Expression = new() { Literal = new(valueType, valueType == NativeScalarType.Null ? "" : "new") } }
        ] };
        var store = new InMemoryFlowOrchestrationStateStore();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store).AugmentAsync(new("schema", "first", DateTimeOffset.UtcNow), Input(xml),
            new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } }, default));
        var checkpoint = Assert.Single(await store.GetAllAsync("schema", default));
        Assert.Equal("Faulted", checkpoint.Native!.State.Status);
        Assert.Equal(xml, checkpoint.Native.State.Messages["request"].Single().Restore().GetText());
        Assert.Equal(xml, checkpoint.Payloads.Single().Restore().GetText());
        Assert.Null(checkpoint.Native.State.EffectNodeId);
    }

    [Theory]
    [InlineData("<!DOCTYPE r [<!ENTITY x 'unsafe'>]><r>&x;</r>")]
    [InlineData("<unfinished>")]
    public async Task NativeConstructionRejectsUnsafeOrInvalidXmlWithoutSavingPartialMessage(string xml)
    {
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [
            new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "request" },
            new() { Kind = NativeFlowNodeKind.CreateXmlMessage, MessageName = "output", Expression = new() { Literal = NativeScalar.From(xml) } }
        ] };
        var store = new InMemoryFlowOrchestrationStateStore();
        await Assert.ThrowsAsync<ArgumentException>(() => new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store).AugmentAsync(new("schema", "first", DateTimeOffset.UtcNow), Input("<r/>"),
            new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } }, default));
        var checkpoint = Assert.Single(await store.GetAllAsync("schema", default));
        Assert.Equal("Faulted", checkpoint.Native!.State.Status);
        Assert.False(checkpoint.Native.State.Messages.ContainsKey("output"));
        Assert.Null(checkpoint.Native.State.EffectNodeId);
    }

    [Fact]
    public async Task NativeMetadataWritePreservesExactKeysPayloadAndIndependentSnapshots()
    {
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [
            new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "request" },
            new() { Kind = NativeFlowNodeKind.SaveMessage, MessageName = "copy" },
            new() { Kind = NativeFlowNodeKind.SetMessageValue, Target = new() { Kind = NativeMessageFactKind.Metadata, MessageName = "request", MetadataKey = "Group" }, Expression = new() { Literal = NativeScalar.From("separate") } },
            new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5 },
            new() { Kind = NativeFlowNodeKind.Reply, MessageName = "request" }
        ] };
        Directory.CreateDirectory(_directory);
        IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Microsoft.Extensions.Options.Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "metadata-state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
        var original = Input("<r/>"); var clock = new Clock();
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };
        var first = new FlowExecutionContext("schema", "first", clock.Now);
        await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(first, original, step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, first.Disposition);
        var saved = Assert.Single(await Store().GetAllAsync("schema", default));
        Assert.Equal("separate", saved.Native!.State.Messages["request"].Single().Restore().Metadata["Group"]);
        clock.Now = clock.Now.AddSeconds(5);
        var context = new FlowExecutionContext("schema", "resumed", clock.Now);
        context.SetOrchestrationInstance(saved.InstanceId, saved.CorrelationKey, saved.Bookmark, true);
        var working = await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(context, IntegrationBatch.Empty, step, default);
        var response = Assert.Single(context.CapturedResponses);
        Assert.Equal("A", response.Metadata["group"]); Assert.Equal("separate", response.Metadata["Group"]);
        Assert.Equal(original.Payloads[0].Name, response.Name); Assert.Equal(original.Payloads[0].ContentType, response.ContentType);
        Assert.Equal(original.Payloads[0].GetText(), response.GetText());
        Assert.False(original.Payloads[0].Metadata.ContainsKey("Group")); Assert.False(working.Payloads[0].Metadata.ContainsKey("Group"));
        var checkpoint = Assert.Single(await Store().GetAllAsync("schema", default));
        Assert.False(checkpoint.Native!.State.Messages["copy"].Single().Metadata.ContainsKey("Group"));
    }

    [Fact]
    public void SnapshotDefaultKeepsLegacyCaseInsensitiveBehaviorAndNativeCaptureOptsInExplicitly()
    {
        var payload = Input("<r/>").Payloads[0];
        var legacy = FlowOrchestrationPayloadSnapshot.Capture(payload);
        Assert.Equal("A", legacy.Restore().Metadata["GROUP"]);
        Assert.DoesNotContain("preserveMetadataCase", JsonSerializer.Serialize(legacy, Json));
        payload = payload with { Metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal) { ["Group"] = "separate" } };
        Assert.Throws<ArgumentException>(() => FlowOrchestrationPayloadSnapshot.Capture(payload));
        var native = FlowOrchestrationPayloadSnapshot.Capture(payload, preserveMetadataCase: true);
        var restored = JsonSerializer.Deserialize<FlowOrchestrationPayloadSnapshot>(JsonSerializer.Serialize(native, Json), Json)!.Restore();
        Assert.Equal("A", restored.Metadata["group"]); Assert.Equal("separate", restored.Metadata["Group"]);
        Assert.False(restored.Metadata.ContainsKey("GROUP"));
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoEffects : IAugmentModuleInvoker, IRenderModuleInvoker
    {
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No source helper/module should be invoked.");
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No source helper/module should be invoked.");
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
