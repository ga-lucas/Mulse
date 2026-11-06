using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativeDynamicSendImportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-dynamic-send-" + Guid.NewGuid().ToString("N"));
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static XElement P(string key, string value) => new(Ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string kind, string name, params object[] children) => new(Ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
    private const string Address = "(Microsoft.XLANGs.BaseTypes.Address)";
    private const string Transport = "(Microsoft.XLANGs.BaseTypes.TransportType)";

    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string assignments, string sendPorts, bool declareDynamic = true)
    {
        Directory.CreateDirectory(_directory);
        var source = new XElement(Ns + "MetaModel", N("ServiceDeclaration", "Service",
            N("MessageDeclaration", "request"),
            N("VariableDeclaration", "target", P("Type", "System.String"), P("InitialValue", "\"\"")),
            N("VariableDeclaration", "kind", P("Type", "System.String"), P("InitialValue", "\"\"")),
            N("PortDeclaration", "dynOut", declareDynamic ? N("PhysicalBindingAttribute", "binding", P("IsDynamic", "True")) : new XComment("static")),
            N("ServiceBody", "Body",
                N("Receive", "input", P("MessageName", "request"), P("PortName", "input")),
                N("VariableAssignment", "Assign", P("Expression", assignments)),
                N("Send", "Out", P("MessageName", "request"), P("PortName", "dynOut")))));
        var project = Path.Combine(_directory, "Dyn.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Dyn</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Dyn.odx'/></ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(_directory, "Dyn.odx"), "<?xml version='1.0'?>" + source);
        var binding = Path.Combine(_directory, "bindings.xml");
        await File.WriteAllTextAsync(binding, $"""
            <BindingInfo><ModuleRefCollection><ModuleRef Name="Dyn"><Services><Service Name="Dyn.Dyn"><Ports>
            <Port Name="input"><ReceivePortRef Name="Input"/></Port><Port Name="dynOut"><SendPortRef Name="DynamicOutput"/></Port>
            </Ports></Service></Services></ModuleRef></ModuleRefCollection>
            <ReceivePortCollection><ReceivePort Name="Input"><ReceiveLocations><ReceiveLocation Name="Source"><Address>C:\synthetic\input</Address><TransportType Name="FILE"/></ReceiveLocation></ReceiveLocations></ReceivePort></ReceivePortCollection>
            <SendPortCollection>{sendPorts}</SendPortCollection></BindingInfo>
            """);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest
            { SourcePath = project, AdditionalBindingPaths = [binding], GenerateNativeFlow = true }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    private static string DynamicPort(params string[] handlers)
        => $"<SendPort Name=\"DynamicOutput\" IsStatic=\"false\"><DynamicSendHanders>{string.Concat(handlers.Select(handler => $"<SendHandler Name=\"Host\"><TransportType Name=\"{handler}\"/></SendHandler>"))}</DynamicSendHanders></SendPort>";

    private const string Both = "dynOut" + Address + " = target; dynOut" + Transport + " = kind;";

    [Fact]
    public async Task DynamicSendPortBecomesExplicitTransportDispatchWithEditableDeliverNodes()
    {
        var (candidate, definition) = await Import(Both, DynamicPort("FILE", "FTP", "HTTP", "SMTP"));
        var all = definition.AllNodes().ToArray();
        Assert.DoesNotContain(all, node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.False(candidate.DraftFlow.Enabled); Assert.Empty(candidate.DraftFlow.Deliveries);
        var declared = all.Where(node => node.Kind == NativeFlowNodeKind.DeclareVariable).Select(node => node.Variable).ToArray();
        Assert.Contains("dynOut_Address", declared); Assert.Contains("dynOut_TransportType", declared);
        Assert.Equal(new[] { "dynOut_Address", "dynOut_TransportType" }, all.Where(node => node.Kind == NativeFlowNodeKind.SetVariable && node.Variable!.StartsWith("dynOut_", StringComparison.Ordinal)).Select(node => node.Variable!));
        var branches = all.Where(node => node.Kind == NativeFlowNodeKind.If && node.Id.Contains("-when-", StringComparison.Ordinal)).Select(node => node.Name).ToArray();
        Assert.Equal(new[] { "FILE", "FTP", "HTTP", "SMTP" }, branches);
        var delivers = all.Where(node => node.Kind == NativeFlowNodeKind.Deliver).ToDictionary(node => node.Module!);
        Assert.Equal(new[] { "file-system-deliver", "ftp-deliver", "http-deliver", "smtp-email-deliver" }, delivers.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "fileName", "path" }, delivers["file-system-deliver"].SettingExpressions!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "host", "port", "remoteFileName", "remotePath" }, delivers["ftp-deliver"].SettingExpressions!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "url" }, delivers["http-deliver"].SettingExpressions!.Keys);
        Assert.Equal(new[] { "to" }, delivers["smtp-email-deliver"].SettingExpressions!.Keys);
        Assert.All(delivers.Values, deliver => Assert.Equal("request", deliver.MessageName));
        Assert.Equal(new[] { "Mail", "Path", "Path", "Url", "Url", "Url", "Url" }, all.Where(node => node.Module == "address-part-transform").Select(node => node.Settings["format"]).Order(StringComparer.Ordinal));
        Assert.Single(all, node => node.Kind == NativeFlowNodeKind.Terminate && node.TerminateMessage!.Contains("no native adapter", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.ConfigurationRequirements, requirement => requirement.SettingPath.EndsWith("settings.password", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("explicit native transport dispatch", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("are NOT translated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DynamicHandlersRestrictBranchesAndUnsupportedAllowedTransportsAreReported()
    {
        var (candidate, definition) = await Import(Both, DynamicPort("FILE", "SFTP"));
        var all = definition.AllNodes().ToArray();
        Assert.Equal(["FILE"], all.Where(node => node.Id.Contains("-when-", StringComparison.Ordinal)).Select(node => node.Name));
        Assert.DoesNotContain(all, node => node.Module == "ftp-deliver");
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("no native adapter (SFTP)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DynamicPortWithOnlyUnsupportedHandlersStaysBlocked()
    {
        var (_, definition) = await Import(Both, DynamicPort("AzureBlobStorage"));
        var blocked = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked && node.Name.Contains("Out", StringComparison.Ordinal));
        Assert.Contains("allows no transport with a native adapter", blocked.BlockReason!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownDynamicPortPropertyAndUndeclaredDynamicPortStayBlocked()
    {
        var (_, unknown) = await Import("dynOut(Microsoft.XLANGs.BaseTypes.Other) = target;", DynamicPort("FILE"));
        Assert.Contains(unknown.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        var (_, undeclared) = await Import(Both, DynamicPort("FILE"), declareDynamic: false);
        Assert.Contains(undeclared.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.DoesNotContain(undeclared.AllNodes(), node => node.Kind == NativeFlowNodeKind.Deliver);
    }

    [Theory]
    [InlineData("nsoftware.SFTP 2022", "sftp-deliver")]
    [InlineData("nsoftware.FTP 2022", "ftp-deliver")]
    public async Task StaticNsoftwareTransportsImportAsNativeDeliverNodes(string transport, string module)
    {
        var (candidate, definition) = await Import("target = \"x\";",
            $"<SendPort Name=\"DynamicOutput\"><PrimaryTransport><Address>files.example.test/out</Address><TransportType Name=\"{transport}\"/></PrimaryTransport></SendPort>", declareDynamic: false);
        var deliver = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Deliver);
        Assert.Equal(module, deliver.Module);
        Assert.False(candidate.DraftFlow.Enabled);
    }

    private sealed class Recorder : IAugmentModuleInvoker, IRenderModuleInvoker, IDeliverModuleInvoker, IModuleInvoker
    {
        public List<(string Module, Dictionary<string, string> Settings)> Deliveries { get; } = [];
        public Task<JsonNode?> InvokeTransformAsync(string id, FlowExecutionContext context, IReadOnlyList<JsonNode?> inputs, IReadOnlyDictionary<string, string> settings, CancellationToken token)
            => id == "address-part-transform" ? new AddressPartTransformModule().InvokeAsync(context, inputs, new() { Module = id, Settings = new(settings) }, token) : throw new NotSupportedException(id);
        public Task<IntegrationBatch> RenderAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new NotSupportedException();
        public Task<IntegrationBatch> AugmentAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new NotSupportedException();
        public Task<IntegrationBatch> DeliverAsync(string module, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token)
        { Deliveries.Add((module, new(settings, StringComparer.OrdinalIgnoreCase))); return Task.FromResult(batch); }
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); }

    private static async Task<(Recorder Recorder, Exception? Failure)> Execute(NativeFlowDefinition definition)
    {
        var recorder = new Recorder(); var clock = new Clock();
        var json = System.Text.Json.JsonSerializer.Serialize(definition, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        var module = new NativeControlFlowAugmentModule(recorder, recorder, new InMemoryFlowOrchestrationStateStore(), clock, recorder, recorder);
        var batch = new IntegrationBatch([new("in.xml", BinaryData.FromString("<x/>"), "application/xml", new Dictionary<string, string> { ["messageId"] = "m1", ["group"] = "g", ["channel"] = "input" })]);
        try { await module.AugmentAsync(new FlowExecutionContext("native", Guid.NewGuid().ToString("N"), clock.GetUtcNow()), batch, new() { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = json } }, default); return (recorder, null); }
        catch (Exception exception) { return (recorder, exception); }
    }

    [Theory]
    [InlineData("ftp://files.example.test:2121/out/x.txt", "FTP", "ftp-deliver", "host", "files.example.test")]
    [InlineData("ftp://files.example.test/out/x.txt", "FTP", "ftp-deliver", "port", "21")]
    [InlineData("C:\\drop\\x.txt", "FILE", "file-system-deliver", "fileName", "x.txt")]
    [InlineData("mailto:ops@example.test", "SMTP", "smtp-email-deliver", "to", "ops@example.test")]
    [InlineData("https://api.example.test/in", "HTTP", "http-deliver", "url", "https://api.example.test/in")]
    public async Task ImportedDispatchRunsTheAssignedTransportWithComputedSettings(string address, string kind, string module, string key, string expected)
    {
        var script = $"target = \"{address.Replace("\\", "\\\\")}\"; kind = \"{kind}\"; {Both}";
        var (_, definition) = await Import(script, DynamicPort("FILE", "FTP", "HTTP", "SMTP"));
        var (recorder, failure) = await Execute(definition);
        Assert.Null(failure);
        var delivery = Assert.Single(recorder.Deliveries);
        Assert.Equal(module, delivery.Module); Assert.Equal(expected, delivery.Settings[key]);
    }

    [Theory]
    [InlineData("AS2")]
    [InlineData("ftp")]
    [InlineData("")]
    public async Task UnassignedOrUnsupportedTransportTerminatesWithoutDelivering(string kind)
    {
        var (_, definition) = await Import($"target = \"ftp://h/x\"; kind = \"{kind}\"; {Both}", DynamicPort("FILE", "FTP"));
        var (recorder, _) = await Execute(definition);
        Assert.Empty(recorder.Deliveries);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
