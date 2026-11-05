using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mulse.CompatibilityPack;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.Orchestration;

namespace Service.Tests;

public sealed class OdxOutboundRuntimeTests
{
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string type, string name, params object[] contents) => new(Ns + "Element", new XAttribute("Type", type), new XAttribute("OID", name), P("Name", name), contents);
    private static OdxProgram Program() => OdxProgramReader.Read("<?xml version='1.0'?>" + new XElement(Ns + "MetaModel", N("Module", "Example",
        N("CorrelationType", "GroupType", N("PropertyRef", "Group", P("Ref", "group"))),
        N("ServiceDeclaration", "Service",
            N("CorrelationDeclaration", "group", P("Type", "Example.GroupType"), P("ParamDirection", "In"),
                N("StatementRef", "init", P("Ref", "outbound"), P("Initializes", "True")),
                N("StatementRef", "follow", P("Ref", "ack"), P("Initializes", "False"))),
            N("ServiceBody", "Body", N("Receive", "input", P("PortName", "input"), P("MessageName", "request"), P("Activate", "True")),
                N("Send", "outbound", P("PortName", "out"), P("MessageName", "request")),
                N("Receive", "ack", P("PortName", "input"), P("MessageName", "ack"), P("Activate", "False")),
                N("Send", "reply", P("PortName", "input"), P("MessageName", "request"), P("OperationMessageName", "Response")))))));
    private static IEnumerable<OdxNode> Nodes(IEnumerable<OdxNode> roots) => roots.SelectMany(node => new[] { node }.Concat(Nodes(node.Children)));
    private static ModuleStepDefinition Step(OdxProgram program, string map) => new()
    {
        Module = OdxProgramAugmentModule.ModuleId, Settings = new()
        {
            ["programJson"] = JsonSerializer.Serialize(program, JsonSerializerOptions.Web), ["inputPort"] = "input", ["messageIdMetadataKey"] = "messageId",
            ["correlationMetadataKey"] = "group", ["replyPort"] = "input", ["outputMessage"] = "request",
            ["sendsJson"] = JsonSerializer.Serialize(new[] { new OdxSendBinding
            {
                NodeId = Nodes(program.Roots).Single(node => node.Name == "outbound").Id, Module = "destination", Settings = new() { ["target"] = "synthetic" },
                Pipeline = [new("xslt-render", new Dictionary<string, string> { ["xsltPath"] = map }), new("observe", new Dictionary<string, string> { ["name"] = "last" })]
            } }, JsonSerializerOptions.Web)
        }
    };
    private static IntegrationBatch Input(string id) => new([new(id + ".xml", BinaryData.FromString("<Input><Value>42</Value></Input>"), "application/xml",
        new Dictionary<string, string> { ["group"] = "A", ["messageId"] = id })]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutboundPipelineRunsUnderLeasesAndPinsCorrelationAcrossRestart_WithoutMutatingTheReply(bool ambiguousDelivery)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-outbound-odx-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var map = Path.Combine(directory, "map.xslt");
            await File.WriteAllTextAsync(map, """
                <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output method="xml" omit-xml-declaration="yes"/>
                <xsl:template match="/"><Wire><xsl:value-of select="/Input/Value"/></Wire></xsl:template></xsl:stylesheet>
                """);
            var program = Program(); var step = Step(program, map); var stages = new List<string>(); var calls = 0;
            var catalog = new FakeExecutableModuleCatalog().AddRender(new XsltRenderModule())
                .AddRender(new DelegateRenderModule("observe", (_, batch, settings, _) =>
                {
                    Assert.Equal("last", settings.Settings["name"]); Assert.Equal("Wire", XDocument.Parse(batch.Payloads.Single().GetText()).Root!.Name.LocalName);
                    stages.Add("pipeline"); return Task.FromResult(batch);
                }))
                .AddDeliver(new DelegateDeliverModule("destination", (_, batch, settings, _) =>
                {
                    calls++; stages.Add("deliver"); Assert.Equal("synthetic", settings.Settings["target"]);
                    Assert.Equal("42", XDocument.Parse(batch.Payloads.Single().GetText()).Root!.Value);
                    if (ambiguousDelivery) throw new IOException("Synthetic ambiguous transport acknowledgement");
                    return Task.CompletedTask;
                }));
            await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
            FlowOrchestrationStateStore Store() => new(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }),
                new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
            OdxProgramAugmentModule Module() => new(Store(), TimeProvider.System, new Service.Modules.RenderModuleInvoker(provider), new Service.Modules.DeliverModuleInvoker(provider));
            FlowExecutionContext Context(string id)
            {
                var context = new FlowExecutionContext("outbound-odx", id, DateTimeOffset.UtcNow);
                context.RegisterSourceReceipt(new(id, "source", new() { Module = "source" }, new()));
                return context;
            }
            var initial = Context("first");
            if (ambiguousDelivery)
            {
                await Assert.ThrowsAsync<OdxAmbiguousEffectException>(() => Module().AugmentAsync(initial, Input("first"), step, default));
                await Assert.ThrowsAsync<OdxAmbiguousEffectException>(() => Module().AugmentAsync(Context("retry"), Input("first"), step, default));
                Assert.True(Assert.Single(await Store().GetAllAsync("outbound-odx", default)).Odx!.RecoveryPending);
            }
            else
            {
                await Module().AugmentAsync(initial, Input("first"), step, default);
                Assert.Equal(FlowExecutionDisposition.Suspended, initial.Disposition);
                Assert.Empty(initial.CapturedResponses); Assert.Single((await Store().GetAllAsync("outbound-odx", default)).Single().SourceReceipts);
                // A restart must resume at the following receive, not re-read/re-render the outbound pipeline.
                await File.WriteAllTextAsync(map, "This must never be rendered again.");
                var completed = Context("ack");
                var output = await Module().AugmentAsync(completed, Input("ack"), step, default);
                Assert.Equal(FlowExecutionDisposition.Continue, completed.Disposition);
                Assert.Equal("Input", XDocument.Parse(Assert.Single(output.Payloads).GetText()).Root!.Name.LocalName);
                Assert.Equal("first.xml", output.Payloads[0].Name);
                Assert.Equal("first.xml", Assert.Single(completed.CapturedResponses).Name);
                Assert.Equal(2, completed.SourceReceipts.Count); Assert.NotNull(completed.PendingCheckpointCompletion);
            }
            Assert.Equal(1, calls); Assert.Equal(new[] { "pipeline", "deliver" }, stages);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"nodeId\":1,\"module\":\"deliver\",\"ignored\":true}]")]
    [InlineData("[{\"nodeId\":0,\"module\":\"deliver\"}]")]
    [InlineData("[{\"nodeId\":1,\"module\":\" deliver\"}]")]
    [InlineData("[{\"nodeId\":1,\"module\":\"deliver\",\"settings\":{\"expectsResponse\":\"invalid\"}}]")]
    [InlineData("[{\"nodeId\":1,\"module\":\"deliver\",\"pipeline\":[null]}]")]
    [InlineData("[{\"nodeId\":1,\"module\":\"deliver\",\"pipeline\":[{\"module\":\"render\",\"settings\":null}]}]")]
    [InlineData("[{\"nodeId\":1,\"module\":\"deliver\",\"pipeline\":[{\"module\":\"render-dispatch\",\"settings\":{}}]}]")]
    [InlineData("[{\"nodeId\":1,\"module\":\"deliver\"},{\"nodeId\":1,\"module\":\"deliver\"}]")]
    public void InvalidOrIgnoredOutboundConfigurationIsRejected(string json) => Assert.Throws<ArgumentException>(() => OdxSendBindings.Read(json));

    private sealed class ResponseDelivery : IRequestResponseDeliverModule
    {
        public ModuleDescriptor Descriptor { get; } = new("two-way", "Two way", ModuleKind.Deliver, "Synthetic response");
        public int Calls { get; private set; }
        public Task DeliverAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken token)
            => throw new InvalidOperationException("The explicitly requested response must not be discarded.");
        public Task<IntegrationBatch> DeliverAndCaptureResponseAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken token)
        {
            Calls++; return Task.FromResult(Input("response"));
        }
    }

    [Fact]
    public async Task LeasedRequestResponseTransportPreservesTheResponse_AndRejectsUnsupportedContractsBeforeDelivery()
    {
        var response = new ResponseDelivery(); var oneWayCalls = 0;
        var catalog = new FakeExecutableModuleCatalog().AddDeliver(response).AddDeliver(new DelegateDeliverModule("one-way", (_, _, _, _) =>
        {
            oneWayCalls++; return Task.CompletedTask;
        }));
        await using var provider = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        var invoker = new Service.Modules.DeliverModuleInvoker(provider); var program = Program();
        var send = Nodes(program.Roots).Single(node => node.Name == "outbound");
        var context = new FlowExecutionContext("response", "run", DateTimeOffset.UtcNow);
        var frame = new OdxExecutionFrame(program, context, IntegrationBatch.Empty, new()); frame.Messages["request"] = Input("request");
        var binding = new OdxSendBinding { NodeId = send.Id, Module = "two-way", Settings = new() { ["expectsResponse"] = "true" } };
        await OdxSendBindings.Bind(send, binding, new Service.Modules.RenderModuleInvoker(provider), invoker)(send, frame, default);
        Assert.Equal(1, response.Calls); Assert.Equal("response.xml", Assert.Single(context.CapturedResponses).Name);
        binding.Module = "one-way";
        await Assert.ThrowsAsync<NotSupportedException>(() => OdxSendBindings.Bind(send, binding, new Service.Modules.RenderModuleInvoker(provider), invoker)(send, frame, default));
        Assert.Equal(0, oneWayCalls);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void OutboundPipelineEnforcesTheExactStageLimit(int count, bool reject)
    {
        var json = JsonSerializer.Serialize(new[] { new OdxSendBinding { NodeId = 1, Module = "deliver",
            Pipeline = Enumerable.Range(0, count).Select(_ => new OdxPipelineStage("render", new Dictionary<string, string>())).ToArray() } }, JsonSerializerOptions.Web);
        if (reject) Assert.Throws<ArgumentException>(() => OdxSendBindings.Read(json)); else Assert.Equal(count, OdxSendBindings.Read(json).Single().Pipeline.Count);
    }

    [Theory]
    [InlineData(128, false)]
    [InlineData(129, true)]
    public void OutboundBindingsEnforceTheExactNodeLimit(int count, bool reject)
    {
        var json = JsonSerializer.Serialize(Enumerable.Range(1, count).Select(id => new OdxSendBinding { NodeId = id, Module = "deliver" }), JsonSerializerOptions.Web);
        if (reject) Assert.Throws<ArgumentException>(() => OdxSendBindings.Read(json)); else Assert.Equal(count, OdxSendBindings.Read(json).Count);
    }
}
