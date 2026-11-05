using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Mulse.CompatibilityPack;
using Mulse.Modules.Mapping;
using Mulse.Modules.Orchestration;

namespace Service.Tests;

public sealed class OdxMessageBindingTests
{
    private static OdxNode Shape(string type, string expression, IReadOnlyDictionary<string, string>? properties = null)
        => new(1, null, 1, "shape", type, "", properties ?? new Dictionary<string, string> { ["Expression"] = expression }, []);
    private static OdxExecutionFrame Frame() => new(new(1, []), new("odx", "test", DateTimeOffset.UtcNow), IntegrationBatch.Empty, new());
    private static IntegrationBatch Message(string name, string body) => new([new(name, BinaryData.FromString(body), "application/xml", new Dictionary<string, string> { ["group"] = "A" })]);

    [Theory]
    [InlineData("[]")]
    [InlineData(" [ \r\n ] ")]
    public void EmptyMapChoicesAreOptionalOnlyForExplicitOdxMode(string json)
    {
        Assert.Empty(RenderDispatchDefinition.Read(json, allowEmpty: true));
        Assert.Throws<ArgumentException>(() => RenderDispatchDefinition.Read(json));
        Assert.Throws<ArgumentException>(() => RenderDispatchDefinition.Read("null", allowEmpty: true));
        Assert.Throws<ArgumentException>(() => RenderDispatchDefinition.Read("[null]", allowEmpty: true));
    }

    [Fact]
    public async Task ActualGetTypeTransformSyntaxDispatchesAnAllowlistedRealXsltThroughTheHostLease()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-odx-xslt-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "map.xslt");
            await File.WriteAllTextAsync(path, """
                <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform">
                  <xsl:output method="xml" omit-xml-declaration="yes"/>
                  <xsl:template match="/"><Mapped><xsl:value-of select="/Input/Value"/></Mapped></xsl:template>
                </xsl:stylesheet>
                """);
            var catalog = new FakeExecutableModuleCatalog().AddRender(new XsltRenderModule());
            using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
            var invoker = new Service.Modules.RenderModuleInvoker(services);
            var node = Shape("MessageAssignment", "mapType = System.Type.GetType(policy.MapName);\n transform (output) = mapType(input);");
            var binding = OdxMessageBindings.BindDynamicTransform(node,
                [new RenderDispatchCase { Value = "Example.Map, Example", Module = "xslt-render", Settings = new() { ["xsltPath"] = path } }], invoker);
            var frame = Frame(); frame.Messages["input"] = Message("input.xml", "<Input><Value>42</Value></Input>");
            frame.Variables["policy"] = JsonNode.Parse("{\"MapName\":\"Example.Map, Example\"}");
            frame.Variables["output"] = JsonNode.Parse("{\"stale\":true}");
            await binding.Activity(node, frame, default);
            Assert.Equal("42", XDocument.Parse(Assert.Single(frame.Messages["output"].Payloads).GetText()).Root!.Value);
            Assert.False(frame.Variables.ContainsKey("output")); Assert.Equal(new[] { "xslt-render" }, binding.RendererDependencies);
            frame.Variables["policy"]!["MapName"] = "Unreviewed.Map";
            await Assert.ThrowsAsync<InvalidOperationException>(() => binding.Activity(node, frame, default));
            Assert.Equal("42", XDocument.Parse(Assert.Single(frame.Messages["output"].Payloads).GetText()).Root!.Value);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task TransformAndPromotedPropertyWritesAreBoundAsOneCompleteShapeWithoutChangingInput()
    {
        var calls = 0;
        var catalog = new FakeExecutableModuleCatalog().AddRender(new DelegateRenderModule("map", (_, batch, _, _) => { calls++; return Task.FromResult(batch); }));
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
        var frame = Frame(); frame.Messages["input"] = Message("input", "<Input/>");
        frame.Variables["policy"] = JsonNode.Parse("{\"MapName\":\"Example.Map\"}");
        frame.Variables["input"] = JsonNode.Parse("{\"Configuration\":{\"User\":\"synthetic-user\",\"Token\":\"synthetic-token\"}}");
        var node = Shape("MessageAssignment", "t = System.Type.GetType(policy.MapName); transform (output) = t(input); // transport context\n output(FTP.UserName) = input.Configuration.User; output(FILE.Password) = input.Configuration.Token;");
        var bound = OdxMessageBindings.BindDynamicTransform(node, [new() { Value = "Example.Map", Module = "map" }], new Service.Modules.RenderModuleInvoker(services));
        await bound.Activity(node, frame, default);
        Assert.Equal(1, calls); Assert.Equal("synthetic-user", frame.Messages["output"].Payloads[0].Metadata["FTP.UserName"]);
        Assert.Equal("synthetic-token", frame.Messages["output"].Payloads[0].Metadata["FILE.Password"]);
        Assert.False(frame.Messages["input"].Payloads[0].Metadata.ContainsKey("FTP.UserName"));
        frame.Variables["input"]!["Configuration"]!["Token"] = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bound.Activity(node, frame, default));
        Assert.Equal(1, calls); Assert.Equal("synthetic-token", frame.Messages["output"].Payloads[0].Metadata["FILE.Password"]);
    }

    [Theory]
    [InlineData("output(FTP.UserName) = Helper.Call();")]
    [InlineData("other(FTP.UserName) = input.User;")]
    [InlineData("output(FTP.UserName) = output.User;")]
    [InlineData("output(FTP.UserName) = output(FILE.Username);")]
    [InlineData("output(FTP.UserName) = input.User; output(ftp.username) = input.User;")]
    public void UnsupportedTransformTailsAreRejectedBeforeAnyRendererInvocation(string tail)
    {
        var node = Shape("MessageAssignment", "t = System.Type.GetType(policy.MapName); transform (output) = t(input); " + tail);
        Assert.Throws<NotSupportedException>(() => OdxMessageBindings.BindDynamicTransform(node, [new() { Value = "map", Module = "map" }], new NoInvoke()));
    }

    [Fact]
    public async Task CountedXmlTrailerConstructionTransformsThroughARealLeasedXsltWithoutStaleInput()
    {
        var path = Path.Combine(Path.GetTempPath(), "mulse-trailer-" + Guid.NewGuid().ToString("N") + ".xslt");
        try
        {
            await File.WriteAllTextAsync(path, """
                <xsl:stylesheet version="1.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform"><xsl:output omit-xml-declaration="yes"/>
                <xsl:template match="/"><Mapped><xsl:value-of select="/Trailer/Count"/></Mapped></xsl:template></xsl:stylesheet>
                """);
            var catalog = new FakeExecutableModuleCatalog().AddRender(new XsltRenderModule());
            using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider();
            var node = Shape("MessageAssignment", """
                // Construct a counted trailer before selecting its map.
                msgXml = new System.Xml.XmlDocument();
                msgXml.LoadXml("<Trailer><Count>" + count.ToString() + "</Count></Trailer>");
                trailer = msgXml;
                tMap = System.Type.GetType(policy.TrailerMapName);
                transform (trailer) = tMap(trailer);
                """);
            var frame = Frame(); frame.Variables["count"] = 7; frame.Variables["policy"] = JsonNode.Parse("{\"TrailerMapName\":\"Example.Trailer\"}");
            frame.Messages["trailer"] = Message("stale", "<Trailer><Count>999</Count></Trailer>");
            var bound = OdxMessageBindings.BindDynamicTransform(node, [new() { Value = "Example.Trailer", Module = "xslt-render", Settings = new() { ["xsltPath"] = path } }],
                new Service.Modules.RenderModuleInvoker(services));
            await bound.Activity(node, frame, default);
            Assert.Equal("7", XDocument.Parse(frame.Messages["trailer"].Payloads.Single().GetText()).Root!.Value);
            Assert.Equal("Trailer", XDocument.Parse(frame.Messages["msgXml"].Payloads.Single().GetText()).Root!.Name.LocalName);
            frame.Variables["count"] = null;
            await Assert.ThrowsAsync<InvalidOperationException>(() => bound.Activity(node, frame, default));
            Assert.Equal("7", XDocument.Parse(frame.Messages["trailer"].Payloads.Single().GetText()).Root!.Value);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("<Trailer>")]
    [InlineData("<!DOCTYPE Trailer [<!ENTITY x 'unsafe'>]><Trailer>&x;</Trailer>")]
    public async Task InvalidConstructedXmlFailsBeforeTheRendererAndPreservesPreviousMessages(string xml)
    {
        var expression = "doc = new System.Xml.XmlDocument(); doc.LoadXml(facts.Xml); trailer = doc; t = System.Type.GetType(facts.Map); transform (trailer) = t(trailer);";
        var node = Shape("MessageAssignment", expression); var frame = Frame();
        frame.Variables["facts"] = new JsonObject { ["Xml"] = xml, ["Map"] = "map" };
        var previous = Message("previous", "<Old/>"); frame.Messages["trailer"] = previous;
        var bound = OdxMessageBindings.BindDynamicTransform(node, [new() { Value = "map", Module = "map" }], new NoInvoke());
        await Assert.ThrowsAsync<ArgumentException>(() => bound.Activity(node, frame, default));
        Assert.Same(previous, frame.Messages["trailer"]); Assert.False(frame.Messages.ContainsKey("doc"));
    }

    [Theory]
    [InlineData("Helper.Call()", "trailer", "facts.Map", "")]
    [InlineData("doc.Xml", "trailer", "facts.Map", "")]
    [InlineData("\"<Trailer/>\"", "other", "facts.Map", "")]
    [InlineData("\"<Trailer/>\"", "trailer", "trailer.Map", "")]
    [InlineData("\"<Trailer/>\"", "trailer", "facts.Map", "Helper.Call();")]
    public void UnsupportedXmlTransformPrefixesAndTailsCannotBindPartially(string xml, string input, string selector, string tail)
    {
        var expression = $"doc = new System.Xml.XmlDocument(); doc.LoadXml({xml}); trailer = doc; t = System.Type.GetType({selector}); transform (trailer) = t({input}); {tail}";
        Assert.Throws<NotSupportedException>(() => OdxMessageBindings.BindDynamicTransform(Shape("MessageAssignment", expression), [new() { Value = "map", Module = "map" }], new NoInvoke()));
    }

    [Fact]
    public async Task MultiMessageMapsKeepSourceArgumentOrder_AndPipelinesPublishOnlyAfterAllStagesSucceed()
    {
        var calls = new List<string>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddRender(new DelegateRenderModule("combine", (_, batch, _, _) => { calls.Add(string.Join(",", batch.Payloads.Select(payload => payload.Name))); return Task.FromResult(batch); }))
            .AddRender(new DelegateRenderModule("fail", (_, _, _, _) => throw new InvalidOperationException("untranslated pipeline stage")));
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).BuildServiceProvider(); var invoker = new Service.Modules.RenderModuleInvoker(services);
        var frame = Frame(); frame.Variables["policy"] = JsonNode.Parse("{\"HeaderMapName\":\"combine\"}");
        frame.Messages["first"] = Message("one", "<A/>"); frame.Messages["second"] = Message("two", "<B/>");
        var node = Shape("MessageAssignment", "tMap = System.Type.GetType(policy.HeaderMapName); transform (combined) = tMap(first, second);");
        var binding = OdxMessageBindings.BindDynamicTransform(node, [new RenderDispatchCase { Value = "combine", Module = "combine" }], invoker);
        await binding.Activity(node, frame, default); Assert.Equal(new[] { "one,two" }, calls);
        var pipeline = OdxMessageBindings.BindPipeline("combined", "final", [new("combine", new Dictionary<string, string>()), new("fail", new Dictionary<string, string>())], invoker);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.Activity(node, frame, default));
        Assert.False(frame.Messages.ContainsKey("final")); Assert.Equal(new[] { "combine", "fail" }, pipeline.RendererDependencies);
    }

    [Fact]
    public async Task MessageCopyKeepsBodiesAndIndependentFacts_AndExplicitRepliesAreCaptured()
    {
        var frame = Frame(); frame.Messages["input"] = Message("source", "<Input/>"); frame.Variables["input"] = JsonNode.Parse("{\"Count\":1}");
        var copyNode = Shape("VariableAssignment", "output = input;");
        await OdxMessageBindings.BindMessageCopy(copyNode)(copyNode, frame, default);
        frame.Variables["output"]!["Count"] = 2;
        Assert.Equal(1, frame.Variables["input"]!["Count"]!.GetValue<int>());
        Assert.NotSame(frame.Messages["input"].Payloads[0].Metadata, frame.Messages["output"].Payloads[0].Metadata);
        var replyNode = Shape("Send", "", new Dictionary<string, string> { ["PortName"] = "request", ["MessageName"] = "output", ["OperationMessageName"] = "Response" });
        await OdxMessageBindings.BindReply(replyNode, "request")(replyNode, frame, default);
        Assert.Equal("<Input/>", Assert.Single(frame.Context.CapturedResponses).GetText());
        Assert.Throws<NotSupportedException>(() => OdxMessageBindings.BindReply(replyNode, "wrong"));
        Assert.Throws<NotSupportedException>(() => OdxMessageBindings.BindSend(replyNode, new Dictionary<string, OdxPortSender> { ["request"] = (_, _, _) => Task.FromResult(IntegrationBatch.Empty) }));
    }

    [Theory]
    [InlineData("t = System.Type.GetType(facts.Map); transform (out) = t(input); Helper.Call();")]
    [InlineData("t = System.Type.GetType(facts.Map); transform (out) = other(input);")]
    [InlineData("t = System.Type.GetType(Helper.GetMap()); transform (out) = t(input);")]
    public void UnsupportedCompoundShapesNeverBindPartially(string expression)
        => Assert.Throws<NotSupportedException>(() => OdxMessageBindings.BindDynamicTransform(Shape("MessageAssignment", expression), [], new NoInvoke()));

    private sealed class NoInvoke : IRenderModuleInvoker
    {
        public Task<IntegrationBatch> RenderAsync(string moduleId, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
            => throw new InvalidOperationException("Invalid source must not invoke a renderer.");
    }
}
