using System.Xml.Linq;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeScopeImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-native-scope-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string kind, string name, params object[] children) => new(Ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
    private static XElement Assign(string name, string expression) => N("VariableAssignment", name, P("Expression", expression));
    private static XElement Catch(string expression, string type = "System.Exception") =>
        N("Catch", "Handle", P("ExceptionName", "ex"), P("ExceptionType", type), P("IsFaultMessage", "False"), Assign("Gather", expression));

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(params XElement[] body)
    {
        Directory.CreateDirectory(_directory);
        var source = new XElement(Ns + "MetaModel", N("ServiceDeclaration", "Service",
            N("MessageDeclaration", "request"),
            N("VariableDeclaration", "result", P("Type", "System.String"), P("InitialValue", "\"\"")),
            N("VariableDeclaration", "failed", P("Type", "System.Boolean"), P("InitialValue", "false")),
            N("ServiceBody", "Body", new object[] { N("Receive", "input", P("MessageName", "request"), P("PortName", "input")) }.Concat(body).ToArray())));
        var project = Path.Combine(_directory, "Scopes.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Scopes</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Scopes.odx'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Scopes.odx"), "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    [Fact]
    public async Task CatchScopeBecomesNativeTryWithExceptionVariablesAndHonestWarnings()
    {
        var (candidate, definition) = await Import(N("Scope", "Work",
            N("LongRunningTransaction", "Tx"), N("TransactionAttribute", "Attr", P("Retry", "True"), P("Timeout", "60")),
            Assign("Run", "result = \"ok\";"),
            Catch("failed = true; result = \"Exception occurred: \" + ex.Message + \", \" + ex.StackTrace;")));
        var all = definition.AllNodes().ToArray();
        Assert.DoesNotContain(all, node => node.Kind == NativeFlowNodeKind.Blocked);
        var attempt = Assert.Single(all, node => node.Kind == NativeFlowNodeKind.Try);
        Assert.Equal("ex_Message", attempt.ExceptionMessageVariable); Assert.Equal("ex_StackTrace", attempt.ExceptionDetailsVariable);
        Assert.Contains(attempt.Children, node => node.Name.StartsWith("LongRunningTransaction (not enforced)", StringComparison.Ordinal));
        Assert.Contains(attempt.Children, node => node.Name.Contains("Retry=True; Timeout=60", StringComparison.Ordinal));
        var handler = Assert.Single(attempt.Otherwise);
        var sets = handler.Children.SelectMany(child => child.Children.Prepend(child)).Where(node => node.Kind == NativeFlowNodeKind.SetVariable).ToArray();
        Assert.Equal(2, sets.Length);
        Assert.Contains(sets, node => node.Expression!.ToString()!.Length > 0 && JsonContains(node, "ex_Message") && JsonContains(node, "ex_StackTrace"));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("transaction declarations", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("not a CLR stack trace", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("Terminate, failed Deliver", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ScopeWithoutCatchIsAPlainSequenceAndScopeLocalScalarsAreDeclared()
    {
        var (candidate, definition) = await Import(N("Scope", "Work", N("VariableDeclaration", "local", P("Type", "System.Int32"), P("InitialValue", "3")),
            Assign("Run", "local = local + 1;")));
        var all = definition.AllNodes().ToArray();
        Assert.DoesNotContain(all, node => node.Kind is NativeFlowNodeKind.Blocked or NativeFlowNodeKind.Try);
        Assert.Contains(all, node => node.Kind == NativeFlowNodeKind.DeclareVariable && node.Variable == "local" && node.VariableType == NativeScalarType.Int32);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("not enforced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnprovenCatchesStayVisibleBlockers()
    {
        var (_, typed) = await Import(N("Scope", "Work", Assign("Run", "result = \"ok\";"), Catch("failed = true;", "System.ApplicationException")));
        Assert.Contains(typed.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Contains("untyped System.Exception", StringComparison.Ordinal));
        Assert.DoesNotContain(typed.AllNodes(), node => node.Kind == NativeFlowNodeKind.Try);
        Directory.Delete(_directory, recursive: true);

        var (_, twice) = await Import(N("Scope", "Work", Assign("Run", "result = \"ok\";"), Catch("failed = true;"), Catch("failed = false;")));
        Assert.Contains(twice.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Contains("several catch handlers", StringComparison.Ordinal));
        Directory.Delete(_directory, recursive: true);

        var (_, assignment) = await Import(N("Scope", "Work", Assign("Run", "result = \"ok\";"), Catch("ex.Message = \"x\";")));
        Assert.Contains(assignment.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.BlockReason.Contains("read-only", StringComparison.Ordinal));
        Directory.Delete(_directory, recursive: true);

        var (_, outside) = await Import(N("Scope", "Work", Assign("Run", "result = ex.Message;"), Catch("failed = true;")));
        Assert.Contains(outside.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
    }

    private static bool JsonContains(NativeFlowNode node, string text) => System.Text.Json.JsonSerializer.Serialize(node.Expression).Contains(text, StringComparison.Ordinal);

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
