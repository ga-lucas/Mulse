using System.Xml.Linq;
using Service.Models;

namespace Service.Tests;

public sealed class BizTalkTransportMappingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-transport-map-" + Guid.NewGuid().ToString("N"));

    private static string Props(string inner) => $"<CustomProps>{inner}</CustomProps>";

    private static XElement SendPort(string name, string transport, string address, string props, string extra = "")
        => XElement.Parse($"<SendPort Name=\"{name}\"><PrimaryTransport><Address>{address}</Address><TransportType Name=\"{transport}\"/></PrimaryTransport>{extra}</SendPort>")
            .Apply(port => port.Element("PrimaryTransport")!.Add(new XElement("TransportTypeData", props)));

    private async Task<BizTalkFlowCandidateResponse> AnalyzeAsync(string bindingXml)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Billing.btproj");
        File.WriteAllText(project, "<Project><PropertyGroup><AssemblyName>Example.Billing</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?><om:MetaModel xmlns:om='http://schemas.microsoft.com/BizTalk/2003/DesignerData'><om:Element Type='Send'/></om:MetaModel>");
        var binding = Path.Combine(_directory, "bindings.xml");
        File.WriteAllText(binding, bindingXml);
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, AdditionalBindingPaths = [binding] }, default);
        return result.FlowCandidates.First();
    }

    [Fact]
    public async Task NsoftwareSftp_IsMappedToSftpDeliver_WithSecretsAndHostKeyRequirement()
    {
        var config = "<Config><SSHHost>sftp.example.com</SSHHost><SSHPort>2222</SSHPort><RemotePath>/out</RemotePath><RemoteFile>%SourceFileName%</RemoteFile><Overwrite>True</Overwrite><SSHPassword>******</SSHPassword><SSHAcceptServerHostKey><AcceptAny>True</AcceptAny></SSHAcceptServerHostKey></Config>";
        var port = SendPort("SftpOut", "nsoftware.SFTP 2022", "sftp://x", Props($"<AdapterConfig>{System.Security.SecurityElement.Escape(config)}</AdapterConfig>"));
        var candidate = await AnalyzeAsync($"<BindingInfo><ModuleRefCollection><ModuleRef Name=\"Example.Billing\"><Services><Service Name=\"A.Flow\"><Ports><Port Name=\"p\"><SendPortRef Name=\"SftpOut\"/></Port></Ports></Service></Services></ModuleRef></ModuleRefCollection><SendPortCollection>{port}</SendPortCollection></BindingInfo>");

        var deliver = candidate.DraftFlow.Deliveries.Single().Delivers.Single().Deliver;
        Assert.Equal("sftp-deliver", deliver.Module);
        Assert.Equal("sftp.example.com", deliver.Settings["host"]);
        Assert.Equal("2222", deliver.Settings["port"]);
        Assert.Equal("/out", deliver.Settings["remotePath"]);
        Assert.Equal("{{payloadName}}", deliver.Settings["remoteFileName"]);
        Assert.Equal("true", deliver.Settings["overwrite"]);
        Assert.Equal("false", deliver.Settings["acceptAnyHostKey"]);
        Assert.StartsWith("{{secret:", deliver.Settings["password"], StringComparison.Ordinal);
        Assert.StartsWith("{{secret:", deliver.Settings["username"], StringComparison.Ordinal);
        Assert.Contains(candidate.DraftFlow.ConfigurationRequirements, requirement => requirement.SettingPath.EndsWith("hostKeyFingerprint", StringComparison.Ordinal));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("accepted ANY SFTP host key", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Smtp_ReadsHostPortAndAddresses_AndFlagsCredentials()
    {
        var port = SendPort("Mail", "SMTP", "mailto:x", Props("<SMTPHost>smtp.example.com:587</SMTPHost><To>a@example.com</To><From>b@example.com</From><Subject>Hello</Subject><Username>u</Username><SMTPAuthenticate>1</SMTPAuthenticate>"));
        var candidate = await AnalyzeAsync($"<BindingInfo><ModuleRefCollection><ModuleRef Name=\"Example.Billing\"><Services><Service Name=\"A.Flow\"><Ports><Port Name=\"p\"><SendPortRef Name=\"Mail\"/></Port></Ports></Service></Services></ModuleRef></ModuleRefCollection><SendPortCollection>{port}</SendPortCollection></BindingInfo>");

        var settings = candidate.DraftFlow.Deliveries.Single().Delivers.Single().Deliver.Settings;
        Assert.Equal("smtp.example.com", settings["host"]);
        Assert.Equal("587", settings["port"]);
        Assert.Equal("true", settings["enableSsl"]);
        Assert.Equal("a@example.com", settings["to"]);
        Assert.Equal("Hello", settings["subject"]);
        Assert.StartsWith("{{secret:", settings["password"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FileReceive_UsesMaskAndPollingInterval_AndSecondaryTransportIsReported()
    {
        var port = SendPort("Out", "FILE", "C:\\out", Props("<FileName>Import_%MessageID%.xml</FileName>"),
            "<SecondaryTransport><Address>C:\\backup</Address><TransportType Name=\"FILE\"/></SecondaryTransport>");
        var receive = new XElement("ReceiveLocation", new XAttribute("Name", "In"),
            new XElement("Address", "C:\\in\\*.billing"),
            new XElement("TransportType", new XAttribute("Name", "FILE")),
            new XElement("TransportTypeData", Props("<FileMask>*.billing</FileMask><PollingInterval>60000</PollingInterval>")));
        var candidate = await AnalyzeAsync($"<BindingInfo><ModuleRefCollection><ModuleRef Name=\"Example.Billing\"><Services><Service Name=\"A.Flow\"><Ports><Port Name=\"p\"><SendPortRef Name=\"Out\"/><ReceivePortRef Name=\"InPort\"/></Port></Ports></Service></Services></ModuleRef></ModuleRefCollection><ReceivePortCollection><ReceivePort Name=\"InPort\"><ReceiveLocations>{receive}</ReceiveLocations></ReceivePort></ReceivePortCollection><SendPortCollection>{port}</SendPortCollection></BindingInfo>");

        var fetch = candidate.DraftFlow.Fetches.Single().Fetch;
        Assert.Equal("file-system-fetch", fetch.Module);
        Assert.Equal("*.billing", fetch.Settings["searchPattern"]);
        Assert.Equal("C:\\in", fetch.Settings["path"]);
        Assert.Equal(PipelineTriggerMode.Interval, candidate.DraftFlow.Trigger.Mode);
        Assert.Equal(TimeSpan.FromMinutes(1), candidate.DraftFlow.Trigger.Interval);
        var deliver = candidate.DraftFlow.Deliveries.Single().Delivers.Single().Deliver;
        Assert.Equal("Random", deliver.Settings["namingMode"]);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("backup (secondary) transport", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendPipelineComponents_BecomePrefilledBillingAugments_AndUnsupportedOnesWarn()
    {
        const string pipelineData = "<Root><Stages><Stage><Components>"
            + "<Component Name=\"Shs.SpaceTrax2.Interfaces.Core.CPC.HL7TypeCaster\"><Properties /></Component>"
            + "<Component Name=\"Shs.SpaceTrax2.Interfaces.Core.CPC.PS.SetProperty\"><Properties><PropertyName vt=\"8\">FileName</PropertyName><PropertyNamespace vt=\"8\">urn:mime</PropertyNamespace><PropertyValue vt=\"8\">a.hl7</PropertyValue></Properties></Component>"
            + "<Component Name=\"Microsoft.BizTalk.Component.MIME_SMIME_Encoder\"><Properties /></Component>"
            + "</Components></Stage></Stages></Root>";
        var port = SendPort("Out", "FILE", "C:\\out", Props("<FileName>x.xml</FileName>"), $"<SendPipelineData>{System.Security.SecurityElement.Escape(pipelineData)}</SendPipelineData>");
        var candidate = await AnalyzeAsync($"<BindingInfo><ModuleRefCollection><ModuleRef Name=\"Example.Billing\"><Services><Service Name=\"A.Flow\"><Ports><Port Name=\"p\"><SendPortRef Name=\"Out\"/></Port></Ports></Service></Services></ModuleRef></ModuleRefCollection><SendPortCollection>{port}</SendPortCollection></BindingInfo>");

        Assert.Contains(candidate.DraftFlow.Augments, step => step.Module == "hl7-message-type-augment");
        var setProperty = Assert.Single(candidate.DraftFlow.Augments, step => step.Module == "set-context-property-augment");
        Assert.Equal("FileName", setProperty.Settings["propertyName"]);
        Assert.Equal("urn:mime", setProperty.Settings["propertyNamespace"]);
        Assert.Equal("a.hl7", setProperty.Settings["propertyValue"]);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("MIME/S-MIME", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedPipelineComponentsRetainTheirOrderAndUnknownComponentsAreReported()
    {
        string Component(string value) => "<Component Name=\"X.PS.SetProperty\"><Properties><PropertyName>FileName</PropertyName><PropertyNamespace>urn:mime</PropertyNamespace><PropertyValue>" + value + "</PropertyValue></Properties></Component>";
        var data = PipelineData(Component("first") + Component("second") + Component("first") + "<Component Name=\"Example.Unknown\"><Properties /></Component>");
        var port = SendPort("Out", "FILE", "C:\\out", Props("<FileName>x.xml</FileName>"), $"<SendPipelineData>{data}</SendPipelineData>");
        var candidate = await AnalyzeAsync(string.Format(Wrap, "<SendPortRef Name=\"Out\"/>", port));
        var values = candidate.DraftFlow.Augments.Where(step => step.Module == "set-context-property-augment").Select(step => step.Settings["propertyValue"]).ToArray();
        Assert.Equal(new[] { "first", "second", "first" }, values);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("Example.Unknown") && warning.Contains("not been translated"));
    }

    private static string PipelineData(string components)
        => System.Security.SecurityElement.Escape("<Root><Stages><Stage><Components>" + components + "</Components></Stage></Stages></Root>");

    private const string Wrap = "<BindingInfo><ModuleRefCollection><ModuleRef Name=\"Example.Billing\"><Services><Service Name=\"A.Flow\"><Ports><Port Name=\"p\">{0}</Port></Ports></Service></Services></ModuleRef></ModuleRefCollection><SendPortCollection>{1}</SendPortCollection></BindingInfo>";

    [Fact]
    public async Task BtaHl7Assembler_SelectsHl7XmlRender_WithTrimSetting()
    {
        var data = PipelineData("<Component Name=\"Microsoft.BizTalk.Component.BTAHL7.HL7AsmComp\"><Properties><TrailingDelimiterAllowed vt=\"11\">0</TrailingDelimiterAllowed></Properties></Component>");
        var port = SendPort("Out", "FILE", "C:\\out", Props("<FileName>x.hl7</FileName>"), $"<SendPipelineData>{data}</SendPipelineData>");
        var candidate = await AnalyzeAsync(string.Format(Wrap, "<SendPortRef Name=\"Out\"/>", port));

        var render = candidate.DraftFlow.Deliveries.Single().Render;
        Assert.Equal("hl7-xml-render", render.Module);
        Assert.Equal("true", render.Settings["trimTrailingDelimiters"]);
    }

    [Fact]
    public async Task PipelineAugmentAfterAssemblerIsExplicitlyReportedAsReordered()
    {
        var data = PipelineData("<Component Name=\"Microsoft.BizTalk.Component.BTAHL7.HL7AsmComp\"><Properties /></Component>"
            + "<Component Name=\"X.PS.SetProperty\"><Properties><PropertyName>FileName</PropertyName><PropertyNamespace>urn:mime</PropertyNamespace><PropertyValue>a.hl7</PropertyValue></Properties></Component>");
        var port = SendPort("Out", "FILE", "C:\\out", Props("<FileName>x.hl7</FileName>"), $"<SendPipelineData>{data}</SendPipelineData>");
        var candidate = await AnalyzeAsync(string.Format(Wrap, "<SendPortRef Name=\"Out\"/>", port));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("after the HL7 assembler") && warning.Contains("Source order is not preserved"));
    }

    [Fact]
    public async Task SetPropertyFileName_MapsSmtpAttachmentNameMetadataKey()
    {
        var data = PipelineData("<Component Name=\"X.PS.SetProperty\"><Properties><PropertyName vt=\"8\">FileName</PropertyName><PropertyNamespace vt=\"8\">urn:mime</PropertyNamespace><PropertyValue vt=\"8\">a.hl7</PropertyValue></Properties></Component>");
        var port = SendPort("Mail", "SMTP", "mailto:x", Props("<SMTPHost>smtp.example.com</SMTPHost><To>a@example.com</To>"), $"<SendPipelineData>{data}</SendPipelineData>");
        var candidate = await AnalyzeAsync(string.Format(Wrap, "<SendPortRef Name=\"Mail\"/>", port));

        Assert.Equal("{urn:mime}FileName", candidate.DraftFlow.Deliveries.Single().Delivers.Single().Deliver.Settings["attachmentNameMetadataKey"]);
    }

    [Fact]
    public async Task DifferingSendPortPipelines_AreScopedPerRoute()
    {
        var withCaster = PipelineData("<Component Name=\"X.CPC.HL7TypeCaster\"><Properties /></Component>");
        var plain = PipelineData("<Component Name=\"X.Other\"><Properties /></Component>");
        var first = SendPort("A", "FILE", "C:\\a", Props("<FileName>a.xml</FileName>"), $"<SendPipelineData>{plain}</SendPipelineData>");
        var second = SendPort("B", "FILE", "C:\\b", Props("<FileName>b.xml</FileName>"), $"<SendPipelineData>{withCaster}</SendPipelineData>");
        var candidate = await AnalyzeAsync(string.Format(Wrap, "<SendPortRef Name=\"A\"/><SendPortRef Name=\"B\"/>", first.ToString() + second.ToString()));

        var step = Assert.Single(candidate.DraftFlow.Augments, s => s.Module == "hl7-message-type-augment");
        Assert.Contains("route-2", step.Settings["conditionJson"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PipelineFile_ProvidesComponentDefaults_WhenBindingHasNoPipelineData()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "SendX.btp"), "<Document><Stages><Stage CategoryId=\"9d0e4101-4cce-4536-83fa-4a5040674ad6\"><Components><Component><Name>Shs.CPC.PS.SetProperty, Shs, Version=1.0</Name><Properties><Property Name=\"PropertyName\"><Value>FileName</Value></Property><Property Name=\"PropertyNamespace\"><Value>urn:mime</Value></Property><Property Name=\"PropertyValue\"><Value>a.hl7</Value></Property></Properties></Component></Components></Stage></Stages></Document>");
        var project = Path.Combine(_directory, "Billing.btproj");
        File.WriteAllText(project, "<Project><PropertyGroup><AssemblyName>Example.Billing</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/><Pipeline Include='SendX.btp'/></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?><om:MetaModel xmlns:om='http://schemas.microsoft.com/BizTalk/2003/DesignerData'><om:Element Type='Send'/></om:MetaModel>");
        var port = SendPort("Out", "FILE", "C:\\out", Props("<FileName>x.xml</FileName>"), "<TransmitPipeline Name=\"Shs.Pipelines.SendX, Shs, Version=1.0\"/>");
        var binding = Path.Combine(_directory, "bindings.xml");
        File.WriteAllText(binding, string.Format(Wrap, "<SendPortRef Name=\"Out\"/>", port));
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, AdditionalBindingPaths = [binding] }, default);
        var candidate = result.FlowCandidates.First();

        var step = Assert.Single(candidate.DraftFlow.Augments, s => s.Module == "set-context-property-augment");
        Assert.Equal("a.hl7", step.Settings["propertyValue"]);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("SendX.btp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeImport_TranslatesTerminateAndConstantDelay()
    {
        Directory.CreateDirectory(_directory);
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Service", N("ServiceBody", "Body",
            N("Terminate", "bad", P("ErrorMessage", "\"Invalid SMTP Address : Batch\";")),
            N("Terminate", "escaped", P("ErrorMessage", "\"Line\\nTwo\\\"quoted\\\"\";")),
            N("Terminate", "dynamic message", P("ErrorMessage", "someVariable")),
            N("Terminate", "missing message"),
            N("Terminate", "unsupported escape", P("ErrorMessage", "\"\\a\"")),
            N("Terminate", "oversized", P("ErrorMessage", "\"" + new string('x', 2001) + "\"")),
            N("Delay", "short", P("Timeout", "new System.TimeSpan(0,2,30)")),
            N("Delay", "days", P("Timeout", "new TimeSpan(1,2,3,4)")),
            N("Delay", "milliseconds", P("Timeout", "new System.TimeSpan(0,0,0,2,1000)")),
            N("Delay", "factory milliseconds", P("Timeout", "TimeSpan.FromMilliseconds(3000);")),
            N("Delay", "ticks constructor", P("Timeout", "new System.TimeSpan(20000000L);")),
            N("Delay", "ticks factory", P("Timeout", "TimeSpan.FromTicks(30000000)")),
            N("Delay", "ticks limit", P("Timeout", "System.TimeSpan.FromTicks(316224000000000L)")),
            N("Delay", "ticks over limit", P("Timeout", "new TimeSpan(316224010000000)")),
            N("Delay", "fractional ticks", P("Timeout", "TimeSpan.FromTicks(10000001)")),
            N("Delay", "zero ticks", P("Timeout", "TimeSpan.FromTicks(0)")),
            N("Delay", "negative ticks", P("Timeout", "new TimeSpan(-10000000)")),
            N("Delay", "overflow ticks", P("Timeout", "TimeSpan.FromTicks(9223372036854775808)")),
            N("Delay", "tick suffix syntax", P("Timeout", "TimeSpan.FromTicks(10000000UL)")),
            N("Delay", "nested", P("Timeout", "TimeSpan.FromTicks(10000000)"), N("Expression", "nested action", P("Expression", "Custom.Run();"))),
            N("Delay", "long evidence", P("Timeout", new string('x', 5000))),
            N("Delay", "fractional", P("Timeout", "TimeSpan.FromMilliseconds(1500)")),
            N("Delay", "extra syntax", P("Timeout", "TimeSpan.FromSeconds(2);;")),
            N("Delay", "at limit", P("Timeout", "TimeSpan.FromDays(366)")),
            N("Delay", "too long", P("Timeout", "TimeSpan.FromDays(367)")),
            N("Delay", "dynamic", P("Timeout", "dtEndTime.Subtract(System.DateTime.Now)")))));
        var project = Path.Combine(_directory, "Term.btproj");
        File.WriteAllText(project, "<Project><PropertyGroup><AssemblyName>Term</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true }, default);
        var candidate = Assert.Single(result.FlowCandidates);
        var flow = Mulse.Modules.ControlFlow.NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true);
        var terminate = flow.AllNodes().Single(node => node.Name == "bad");
        Assert.Equal(Mulse.Modules.ControlFlow.NativeFlowNodeKind.Terminate, terminate.Kind);
        Assert.Equal("Invalid SMTP Address : Batch", terminate.TerminateMessage);
        var escaped = flow.AllNodes().Single(node => node.Name == "escaped");
        Assert.Equal(Mulse.Modules.ControlFlow.NativeFlowNodeKind.Terminate, escaped.Kind);
        Assert.Equal("Line\nTwo\"quoted\"", escaped.TerminateMessage);
        foreach (var name in new[] { "dynamic message", "missing message", "unsupported escape", "oversized" })
        {
            var blocked = flow.AllNodes().Single(node => node.Name == name);
            Assert.Equal(Mulse.Modules.ControlFlow.NativeFlowNodeKind.Blocked, blocked.Kind);
            Assert.Contains("Terminate message could not be preserved", blocked.BlockReason);
            Assert.Contains(candidate.DraftFlow.Migration!.Diagnostics, diagnostic => diagnostic.Code == "BT-NATIVE-UNRESOLVED" && diagnostic.Message.Contains(name));
        }
        Assert.Contains("someVariable", flow.AllNodes().Single(node => node.Name == "dynamic message").BlockReason);
        Assert.Contains("[truncated]", flow.AllNodes().Single(node => node.Name == "oversized").BlockReason);
        var wait = flow.AllNodes().Single(node => node.Name == "short");
        Assert.Equal(Mulse.Modules.ControlFlow.NativeFlowNodeKind.Wait, wait.Kind);
        Assert.Equal(150, wait.DurationSeconds);
        foreach (var (name, seconds) in new[] { ("days", 93784), ("milliseconds", 3), ("factory milliseconds", 3), ("at limit", 31622400),
            ("ticks constructor", 2), ("ticks factory", 3), ("ticks limit", 31622400) })
        {
            var delay = flow.AllNodes().Single(node => node.Name == name);
            Assert.Equal(Mulse.Modules.ControlFlow.NativeFlowNodeKind.Wait, delay.Kind);
            Assert.Equal(seconds, delay.DurationSeconds);
        }
        foreach (var name in new[] { "fractional", "extra syntax", "too long", "ticks over limit", "fractional ticks", "zero ticks",
            "negative ticks", "overflow ticks", "tick suffix syntax", "nested", "long evidence" })
        {
            var blocked = flow.AllNodes().Single(node => node.Name == name);
            Assert.Equal(Mulse.Modules.ControlFlow.NativeFlowNodeKind.Blocked, blocked.Kind);
            Assert.Contains(candidate.DraftFlow.Migration!.Diagnostics, diagnostic => diagnostic.Code == "BT-NATIVE-UNRESOLVED" && diagnostic.Message.Contains(name));
        }
        var nested = flow.AllNodes().Single(node => node.Name == "nested");
        Assert.Contains(nested.Children, node => node.Name == "nested action" && node.Kind == Mulse.Modules.ControlFlow.NativeFlowNodeKind.Blocked);
        Assert.Contains("[truncated]", flow.AllNodes().Single(node => node.Name == "long evidence").BlockReason);
        var dynamic = flow.AllNodes().Single(node => node.Name == "dynamic");
        Assert.Equal(Mulse.Modules.ControlFlow.NativeFlowNodeKind.Blocked, dynamic.Kind);
        Assert.Contains("dtEndTime", dynamic.BlockReason);
    }

    [Fact]
    public async Task NativeImport_TreatsContractDeclarationsAsNonBlocking()
    {
        Directory.CreateDirectory(_directory);
        XNamespace ns = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(ns + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(ns + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var source = new XElement(ns + "MetaModel", N("ServiceDeclaration", "Service",
            N("MessageDeclaration", "msg"),
            N("PortDeclaration", "inPort", N("PhysicalBindingAttribute", "binding")),
            N("PortType", "InType", N("OperationDeclaration", "Op", N("MessageRef", "Request", P("Ref", "msg")))),
            N("ServiceBody", "Body", N("Construct", "build", N("MessageRef", "constructed", P("Ref", "msg"))),
                N("MessageRef", "unexpected", P("Ref", "msg")), N("Scope", "stop"))));
        var project = Path.Combine(_directory, "Decl.btproj");
        File.WriteAllText(project, "<Project><PropertyGroup><AssemblyName>Decl</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        File.WriteAllText(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var result = await new BizTalkImportService().AnalyzeAsync(new AnalyzeBizTalkSolutionRequest { SourcePath = project, GenerateNativeFlow = true }, default);
        var blockers = Assert.Single(result.FlowCandidates).DraftFlow.Migration!.Diagnostics.Where(d => d.Code == "BT-NATIVE-UNRESOLVED").Select(d => d.Message).ToArray();
        Assert.DoesNotContain(blockers, m => m.StartsWith("PortType") || m.StartsWith("OperationDeclaration") || m.StartsWith("PortDeclaration")
            || m.StartsWith("PhysicalBindingAttribute") || m.StartsWith("MessageRef 'Request'") || m.StartsWith("MessageRef 'constructed'"));
        Assert.Contains(blockers, m => m.StartsWith("MessageRef 'unexpected'"));
        Assert.DoesNotContain(blockers, m => m.StartsWith("Scope"));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}

internal static class XElementExtensions
{
    public static XElement Apply(this XElement element, Action<XElement> action)
    {
        action(element);
        return element;
    }
}
