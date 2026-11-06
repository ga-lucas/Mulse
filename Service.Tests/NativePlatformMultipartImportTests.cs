using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;

namespace Service.Tests;

public sealed class NativePlatformMultipartImportTests : IDisposable
{
    private const string PlatformPrefix = "{urn:mulse:biztalk-platform-property:";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-platform-multipart-" + Guid.NewGuid().ToString("N"));

    private const string Prelude = "hl7.MSHSegment = new System.Xml.XmlDocument(); hl7.BodySegments = new System.Xml.XmlDocument(); hl7.MSHSegment = header; hl7.BodySegments = body; hl7.ZSegments = \"\";";
    private const string CopyPrelude = "copy.MSHSegment = new System.Xml.XmlDocument(); copy.BodySegments = new System.Xml.XmlDocument(); copy.MSHSegment = hl7.MSHSegment; copy.BodySegments = hl7.BodySegments; copy.ZSegments = hl7.ZSegments;";

    private async Task<NativeFlowDefinition> Import(params (string Name, string Expression, string Output)[] constructs)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Fixture.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Fixture</AssemblyName><RootNamespace>Fixture</RootNamespace></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        XElement Part(string name, string clr, string body) => N("PartDeclaration", name, P("ClassName", clr), P("IsBodyPart", body));
        XElement Message(string name, string type) => N("MessageDeclaration", name, P("Type", type));
        var body = new List<object>
        {
            N("Receive", "receiveHeader", P("MessageName", "header"), P("PortName", "inHeader")),
            N("Receive", "receiveBody", P("MessageName", "body"), P("PortName", "inBody"))
        };
        foreach (var (name, expression, output) in constructs)
            body.Add(N("Construct", "construct " + name, N("MessageAssignment", name, P("Expression", expression)), N("MessageRef", "ref " + name, P("Ref", output))));
        var source = new XElement(ns + "MetaModel", N("Module", "Fx",
            N("MultipartMessageType", "HL7vXGeneric", Part("MSHSegment", "System.Xml.XmlDocument", "False"), Part("BodySegments", "System.Xml.XmlDocument", "True"), Part("ZSegments", "System.String", "False")),
            N("ServiceDeclaration", "Flow",
                Message("header", "System.Xml.XmlDocument"), Message("body", "System.Xml.XmlDocument"), Message("hl7", "Fx.HL7vXGeneric"), Message("copy", "Fx.HL7vXGeneric"),
                N("ServiceBody", "Body", body.ToArray()))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new() { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
        Assert.False(candidate.DraftFlow.Enabled);
        return NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true);
    }

    private static NativeFlowNode Block(NativeFlowDefinition definition, string name) => definition.AllNodes().Single(node => node.Name == name);

    [Fact]
    public async Task HeaderAndBodyDocumentsBecomeOneNativeRootWithTwoAppendsAndPlatformWritesAreTypedMetadata()
    {
        var definition = await Import(("fields", Prelude
            + " hl7(BTAHL7Schemas.ToBeBatched) = false; hl7(BTAHL7Schemas.MSH1) = 124; hl7(BTAHL7Schemas.MessageClass) = \"MessageClass2X\"; hl7(BTAHL7Schemas.MSH2) = @\"^~\\&\";", "hl7"));
        var block = Block(definition, "fields");
        Assert.NotEqual(NativeFlowNodeKind.Blocked, block.Kind);
        var nodes = block.Children;
        Assert.Equal([NativeFlowNodeKind.CreateXmlRoot, NativeFlowNodeKind.AppendXmlMessage, NativeFlowNodeKind.AppendXmlMessage],
            nodes.Where(node => node.Kind is NativeFlowNodeKind.CreateXmlRoot or NativeFlowNodeKind.AppendXmlMessage).Select(node => node.Kind));
        Assert.Equal("HL7Multipart", nodes.First(node => node.Kind == NativeFlowNodeKind.CreateXmlRoot).Expression!.Literal!.Value);
        Assert.Equal(["header", "body"], nodes.Where(node => node.Kind == NativeFlowNodeKind.AppendXmlMessage).Select(node => node.SourceMessage));
        Assert.All(nodes.Where(node => node.Kind != NativeFlowNodeKind.SetMessageValue), node => Assert.Equal("hl7", node.MessageName));
        var writes = nodes.Where(node => node.Kind == NativeFlowNodeKind.SetMessageValue).ToDictionary(node => node.Target!.MetadataKey, node => node);
        Assert.Equal(4, writes.Count);
        Assert.All(writes.Values, node => Assert.Equal("hl7", node.Target!.MessageName));
        Assert.All(writes.Keys, key => Assert.StartsWith(PlatformPrefix, key));
        Assert.Equal(NativeMessageFactKind.MetadataBoolean, writes.Single(pair => pair.Key.EndsWith("}ToBeBatched")).Value.Target!.Kind);
        Assert.Equal(NativeMessageFactKind.MetadataInt32, writes.Single(pair => pair.Key.EndsWith("}MSH1")).Value.Target!.Kind);
        Assert.Equal(NativeMessageFactKind.Metadata, writes.Single(pair => pair.Key.EndsWith("}MessageClass")).Value.Target!.Kind);
        Assert.Equal("^~\\&", writes.Single(pair => pair.Key.EndsWith("}MSH2")).Value.Expression!.Literal!.Value);
    }

    [Fact]
    public async Task WholeMessageMultipartCopyBecomesUseAndSave()
    {
        var definition = await Import(("first", Prelude, "hl7"), ("second", CopyPrelude, "copy"));
        var block = Block(definition, "second");
        Assert.NotEqual(NativeFlowNodeKind.Blocked, block.Kind);
        Assert.Equal([NativeFlowNodeKind.UseMessage, NativeFlowNodeKind.SaveMessage], block.Children.Select(node => node.Kind));
        Assert.Equal("hl7", block.Children[0].MessageName); Assert.Equal("copy", block.Children[1].MessageName);
    }

    [Theory]
    [InlineData("hl7.MSHSegment = new System.Xml.XmlDocument(); hl7.BodySegments = new System.Xml.XmlDocument(); hl7.MSHSegment = header; hl7.BodySegments = body; hl7.ZSegments = \"extra\";")]
    [InlineData("hl7.MSHSegment = new System.Xml.XmlDocument(); hl7.BodySegments = new System.Xml.XmlDocument(); hl7.MSHSegment = header; hl7.BodySegments = header; hl7.ZSegments = \"\"; hl7.ZSegments = \"\";")]
    [InlineData(Prelude + " hl7.BodySegments(MIME.FileName) = \"a.txt\";")]
    [InlineData("hl7.MSHSegment = header; hl7.BodySegments = body; hl7.ZSegments = \"\";")]
    [InlineData(Prelude + " hl7(FTP.Password) = \"secret\";")]
    [InlineData(Prelude + " hl7(EDI.Anything) = \"x\";")]
    [InlineData(Prelude + " hl7(BTAHL7Schemas.MSH2) = hl7(BTAHL7Schemas.MSH2);")]
    [InlineData(Prelude + " hl7(BTAHL7Schemas.MSH1) = null;")]
    public async Task UnprovenMultipartPartsPartContextSecretsReadsAndUnknownPlatformNamespacesBlockTheWholeBlock(string expression)
    {
        var definition = await Import(("fields", expression, "hl7"));
        var block = Block(definition, "fields");
        Assert.Equal(NativeFlowNodeKind.Blocked, block.Kind); Assert.Empty(block.Children);
    }

    [Fact]
    public async Task MixedMultipartSourcesAreRejectedBecauseTheyCannotBeRepresentedFaithfully()
    {
        var definition = await Import(("first", Prelude, "hl7"),
            ("second", "copy.MSHSegment = new System.Xml.XmlDocument(); copy.BodySegments = new System.Xml.XmlDocument(); copy.MSHSegment = header; copy.BodySegments = hl7.BodySegments; copy.ZSegments = hl7.ZSegments;", "copy"));
        Assert.Equal(NativeFlowNodeKind.Blocked, Block(definition, "second").Kind);
    }

    [Fact]
    public async Task PlatformAndMultipartApproximationsAreDisclosedAndNeverClaimBizTalkParity()
    {
        Directory.CreateDirectory(_directory);
        await Import(("fields", Prelude + " hl7(BTAHL7Schemas.ToBeBatched) = false;", "hl7"));
        var project = Path.Combine(_directory, "Fixture.btproj");
        var result = await new BizTalkImportService().AnalyzeAsync(new() { SourcePath = project, GenerateNativeFlow = true }, default);
        var warnings = Assert.Single(result.FlowCandidates).DraftFlow.Warnings;
        Assert.Contains(warnings, warning => warning.Contains("metadata", StringComparison.OrdinalIgnoreCase) && warning.Contains("BTAHL7Schemas"));
        Assert.Contains(warnings, warning => warning.Contains("HL7vXGeneric multipart"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
