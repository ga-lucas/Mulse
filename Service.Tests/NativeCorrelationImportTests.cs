using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeCorrelationImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-correlation-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string kind, string name, string oid, params object[] children)
        => new(Ns + "Element", new XAttribute("Type", kind), new XAttribute("OID", oid), P("Name", name), children);

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string declaredType, string statementRef, string initializes, bool declareType = true)
    {
        Directory.CreateDirectory(_directory);
        var source = new XElement(Ns + "MetaModel",
            declareType ? N("CorrelationType", "OrderCorrelationType", "type-1", N("PropertyRef", "PropertyRef_1", "prop-1")) : new XComment("no type"),
            N("ServiceDeclaration", "Service", "service-1",
                N("MessageDeclaration", "request", "message-1"),
                N("CorrelationDeclaration", "OrderSet", "corr-1", P("Type", declaredType),
                    N("StatementRef", "StatementRef_1", "ref-1", P("Initializes", initializes), P("Ref", statementRef))),
                N("ServiceBody", "Body", "body-1",
                    N("Receive", "input", "receive-1", P("MessageName", "request"), P("PortName", "input")),
                    N("Send", "Out", "send-1", P("MessageName", "request"), P("PortName", "out")))));
        var project = Path.Combine(_directory, "Corr.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Corr</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Corr.odx'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Corr.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
            { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    private static IEnumerable<NativeFlowNode> Correlation(NativeFlowDefinition definition)
        => definition.AllNodes().Where(node => node.Name.StartsWith("CorrelationDeclaration", StringComparison.Ordinal) || node.Name.StartsWith("StatementRef", StringComparison.Ordinal));

    [Fact]
    public async Task SetOnlyInitializedBySendIsKeptAsNamedEvidenceNotABlocker()
    {
        var (candidate, definition) = await Import("Corr.OrderCorrelationType", "send-1", "True");
        var nodes = Correlation(definition).ToArray();
        Assert.Equal(2, nodes.Length);
        Assert.DoesNotContain(nodes, node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains(nodes, node => node.Name.Contains("initialized by Send 'Out', never followed", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("promoted properties is not enforced", StringComparison.Ordinal));
        Assert.False(candidate.DraftFlow.Enabled);
    }

    [Theory]
    [InlineData("send-1", "False")]
    [InlineData("receive-1", "True")]
    [InlineData("missing-oid", "True")]
    [InlineData("send-1", "")]
    public async Task FollowedReceiveInitializedOrUnresolvedSetsStayBlocked(string statementRef, string initializes)
    {
        var (_, definition) = await Import("Corr.OrderCorrelationType", statementRef, initializes);
        var declaration = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.Name == "OrderSet");
        Assert.Contains("needs explicit native channel and correlation-key bindings", declaration.BlockReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetWhoseTypeIsNotDeclaredInTheSourceStaysBlocked()
    {
        var (_, definition) = await Import("Other.UnknownCorrelationType", "send-1", "True", declareType: false);
        Assert.Contains(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.Name == "OrderSet");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
