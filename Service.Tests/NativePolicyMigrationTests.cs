using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mulse.Modules.Orchestration;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Decisions;
using Mulse.Modules.Execution;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.BizTalkImport;
using Service.Models;

namespace Service.Tests;

public sealed class NativePolicyMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-native-policy-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static readonly XNamespace Om = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
    private static readonly XNamespace Brl = "http://schemas.microsoft.com/businessruleslanguage/2002";
    private static XElement P(string key, string value) => new(Om + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
    private static XElement N(string kind, string name, params object[] children) => new(Om + "Element", new XAttribute("Type", kind), P("Name", name), children);
    private static IntegrationBatch Input(string group = "001", string batch = "true", bool declaration = false) => new([new("facts.xml",
        BinaryData.FromString((declaration ? "<?xml version='1.0' encoding='utf-16' standalone='yes'?>" : "") + BizTalkRulePolicyTests.Facts(group, batch)),
        "application/xml", new Dictionary<string, string>(StringComparer.Ordinal) { ["group"] = "lower", ["Group"] = "upper", ["channel"] = "in", ["messageId"] = "1" })]);
    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(string? version = "1.52", string? variation = null)
    {
        Directory.CreateDirectory(_directory);
        var project = Path.Combine(_directory, "Test.btproj");
        await File.WriteAllTextAsync(project, "<Project><PropertyGroup><AssemblyName>Test</AssemblyName><RootNamespace>Test</RootNamespace></PropertyGroup><ItemGroup><Schema Include='IntegrationRules.xsd'/><XLang Include='Flow.odx'/></ItemGroup></Project>");
        XNamespace xs = "http://www.w3.org/2001/XMLSchema", bt = "http://schemas.microsoft.com/BizTalk/2003";
        var fields = new[] { ("GroupID", "string"), ("IsBatch", "boolean"), ("MapName", "string") };
        var schema = new XElement(xs + "schema", new XAttribute(XNamespace.Xmlns + "xs", xs), new XAttribute(XNamespace.Xmlns + "b", bt), new XAttribute("targetNamespace", "urn:test"),
            new XElement(xs + "element", new XAttribute("name", "IntegrationRules"),
                new XElement(xs + "annotation", new XElement(xs + "appinfo", new XElement(bt + "properties", fields.Select(field =>
                    new XElement(bt + "property", new XAttribute("distinguished", "true"), new XAttribute("xpath", $"/*[local-name()='IntegrationRules' and namespace-uri()='urn:test']/*[local-name()='{field.Item1}' and namespace-uri()='']")))))),
                new XElement(xs + "complexType", new XElement(xs + "sequence", fields.Select(field =>
                    new XElement(xs + "element", new XAttribute("name", field.Item1), new XAttribute("type", "xs:" + (variation == "schema-type" && field.Item1 == "GroupID" ? "int" : field.Item2))))))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "IntegrationRules.xsd"), schema.ToString());
        var parameter = N("RulesParameterRef", "parameter", P("Reference", "facts"), P("Alias", variation == "alias" ? "Wrong.Type" : "Test.IntegrationRules"));
        if (variation == "nested-parameter") parameter.Add(N("Expression", "nested", P("Expression", "Untranslated.Helper();")));
        var call = N("CallRules", "rules", P("PolicyName", "Any Policy"), P("PolicyVersion", variation == "source-version" ? "1.51" : "-1"), parameter);
        if (variation == "multiple-parameters") call.Add(new XElement(parameter));
        var source = new XElement(Om + "MetaModel", N("ServiceDeclaration", "Flow",
            N("MessageDeclaration", "facts", P("Type", "Test.IntegrationRules")), N("MessageDeclaration", "copy", P("Type", "Test.IntegrationRules")),
            N("ServiceBody", "Body", N("Receive", "receive", P("MessageName", "facts"), P("PortName", "in")),
                N("Construct", "copy-input", N("MessageAssignment", "copy-assignment", P("Expression", "copy = facts;"))), call,
                N("Send", "reply", P("MessageName", "facts"), P("OperationMessageName", "Response")))));
        await File.WriteAllTextAsync(Path.Combine(_directory, "Flow.odx"), "<?xml version='1.0'?>" + source);
        var old = BizTalkRulePolicyTests.Policy(1, 51);
        old.Descendants(Brl + "then").First().Descendants(Brl + "string").Single().Value = "Map.Old";
        var selected = BizTalkRulePolicyTests.Policy(1, 52);
        if (variation == "conflict") selected.Descendants(Brl + "if").Last().Descendants(Brl + "string").Single().Value = "001";
        var rules = Path.Combine(_directory, "rules.xml");
        await File.WriteAllTextAsync(rules, BizTalkRulePolicyTests.Export(old, selected));
        var duplicate = Path.Combine(_directory, "duplicate.xml");
        if (variation == "duplicate-export") await File.WriteAllTextAsync(duplicate, BizTalkRulePolicyTests.Export(selected));
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new()
        {
            SourcePath = project, AdditionalRulePaths = variation == "duplicate-export" ? [rules, duplicate] : [rules], GenerateNativeFlow = true,
            NativePolicyVersions = version is null ? null : new Dictionary<string, string> { ["Any Policy"] = version }
        }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    [Theory]
    [InlineData("1.51", "Map.Old")]
    [InlineData("1.52", "Map.A")]
    public async Task SelectedVersionCompilesExactNamedFactAndRetainsEverySourceNode(string version, string expected)
    {
        var (candidate, definition) = await Import(version);
        Assert.False(candidate.DraftFlow.Enabled);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        var call = Assert.Single(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.ApplyXmlRules);
        Assert.Equal("facts", call.MessageName);
        var policy = XmlRuleRuntime.ReadPolicy(call.PolicyJson!);
        Assert.Equal(version, policy.Version);
        Assert.Equal(expected, policy.Rules[0].Assignments[0].Value);
        var source = OdxProgramReader.ReadFile(Path.Combine(_directory, "Flow.odx"));
        Assert.All(source.Roots.SelectMany(Flatten), node => Assert.Contains(definition.AllNodes(), translated => translated.Id == "source-" + node.Id));
        Assert.DoesNotContain("xml-rules-augment", candidate.DraftFlow.Augments.Select(step => step.Module));
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("not proof of the deployed version", StringComparison.Ordinal));
        static IEnumerable<OdxNode> Flatten(OdxNode node) => new[] { node }.Concat(node.Children.SelectMany(Flatten));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("1.99", null)]
    [InlineData("1.52", "conflict")]
    [InlineData("1.52", "alias")]
    [InlineData("1.52", "schema-type")]
    [InlineData("1.52", "source-version")]
    [InlineData("1.52", "multiple-parameters")]
    [InlineData("1.52", "nested-parameter")]
    [InlineData("1.52", "duplicate-export")]
    public async Task NoSelectionOrUnprovenCallNeverSelectsLatestOrPartiallyExecutes(string? version, string? variation)
    {
        var (candidate, definition) = await Import(version, variation);
        Assert.False(candidate.DraftFlow.Enabled);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.ApplyXmlRules);
        Assert.Equal(NativeFlowNodeKind.Blocked, definition.AllNodes().Single(node => node.Name == "rules").Kind);
        Assert.Contains(definition.AllNodes(), node => node.Name == "parameter");
    }

    [Theory]
    [InlineData("001", "true", "Map.A")]
    [InlineData("002", "1", "Map.B")]
    [InlineData("999", "true", "")]
    [InlineData("001", "false", "")]
    public async Task NativePolicyMutatesOnlyNamedSnapshotAcrossDiskRestart(string group, string batch, string expected)
    {
        var (_, definition) = await Import();
        var parent = definition.AllNodes().Single(node => node.Children.Any(child => child.Kind == NativeFlowNodeKind.Reply));
        parent.Children.Insert(parent.Children.FindIndex(node => node.Kind == NativeFlowNodeKind.Reply), new() { Id = "pause", Kind = NativeFlowNodeKind.Wait, DurationSeconds = 1 });
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };
        var clock = new Clock();
        IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
        var input = Input(group, batch, declaration: true);
        var context = new FlowExecutionContext("policy", "run", clock.Now);
        var output = await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(context, input, step, default);
        Assert.Equal(input.Payloads[0].GetText(), output.Payloads[0].GetText());
        var saved = Assert.Single(await Store().GetAllAsync("policy", default));
        var state = saved.Native!.State;
        Assert.Equal(input.Payloads[0].GetText(), state.Messages["copy"].Single().Restore().GetText());
        Assert.Equal(expected, XDocument.Parse(state.Messages["facts"].Single().Restore().GetText()).Root!.Element("MapName")!.Value);
        clock.Now += TimeSpan.FromSeconds(1);
        var resumed = new FlowExecutionContext("policy", "resume", clock.Now);
        resumed.SetOrchestrationInstance(saved.InstanceId, saved.CorrelationKey, saved.Bookmark, true);
        await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(resumed, IntegrationBatch.Empty, step, default);
        var reply = Assert.Single(resumed.CapturedResponses);
        Assert.Equal(input.Payloads[0].Name, reply.Name); Assert.Equal("application/xml", reply.ContentType);
        Assert.Equal("lower", reply.Metadata["group"]); Assert.Equal("upper", reply.Metadata["Group"]);
        Assert.Equal(4, reply.Metadata.Count);
        Assert.Equal(expected, XDocument.Parse(reply.GetText()).Root!.Element("MapName")!.Value);
        if (expected.Length == 0) Assert.Equal(input.Payloads[0].GetText(), reply.GetText());
        else Assert.Equal("utf-8", XDocument.Parse(reply.GetText()).Declaration!.Encoding);
    }

    [Theory]
    [InlineData("conflicting-rules")]
    [InlineData("missing-target")]
    [InlineData("depth-limit")]
    public async Task InvalidFactsAndConflictsFaultWithoutPartiallyMutatingNamedMessages(string variation)
    {
        var (_, imported) = await Import();
        var node = imported.AllNodes().Single(node => node.Kind == NativeFlowNodeKind.ApplyXmlRules);
        var input = Input();
        if (variation == "conflicting-rules")
        {
            var policy = XmlRuleRuntime.ReadPolicy(node.PolicyJson!);
            policy.Rules[1].Conditions[0].Value = "001";
            node.PolicyJson = JsonSerializer.Serialize(policy, Json);
        }
        else
        {
            var document = XDocument.Parse(input.Payloads[0].GetText());
            if (variation == "missing-target") document.Root!.Element("MapName")!.Remove();
            else
            {
                var nested = new XElement("Unused");
                for (var depth = 0; depth < 65; depth++) nested = new XElement("Unused", nested);
                document.Root!.Add(nested);
            }
            input = new([input.Payloads[0] with { Content = BinaryData.FromString(document.ToString()) }]);
        }
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [new() { Kind = NativeFlowNodeKind.Receive, MessageName = "facts", Channel = "in" }, node] };
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };
        var store = new InMemoryFlowOrchestrationStateStore();
        var context = new FlowExecutionContext("policy-failure", "run", DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<ArgumentException>(() => new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), store).AugmentAsync(context, input, step, default));
        var state = Assert.Single(await store.GetAllAsync("policy-failure", default)).Native!.State;
        Assert.Equal("Faulted", state.Status);
        Assert.Equal(input.Payloads[0].GetText(), state.Messages["facts"].Single().Restore().GetText());
        Assert.Null(state.EffectNodeId);
        Assert.Empty(context.CapturedResponses);
    }

    [Theory]
    [InlineData("1")]
    [InlineData("1.52.0")]
    [InlineData("01.52")]
    [InlineData("1.052")]
    [InlineData("-1")]
    public async Task TargetVersionMustBeExplicitCanonicalMajorMinor(string version)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => new BizTalkImportService().AnalyzeAsync(new()
        { SourcePath = _directory, GenerateNativeFlow = true, NativePolicyVersions = new Dictionary<string, string> { ["Any Policy"] = version } }, default));
    }

    [Theory]
    [InlineData(64, 256, true, true)]
    [InlineData(65, 256, true, false)]
    [InlineData(1, 257, true, false)]
    [InlineData(1, 0, true, false)]
    [InlineData(1, 8, false, false)]
    public async Task TargetCountNameAndNativeGenerationContractsAreEnforced(int count, int nameLength, bool native, bool valid)
    {
        await Import();
        var targets = Enumerable.Range(0, count).ToDictionary(index => (nameLength == 0 ? "" : index.ToString().PadLeft(nameLength, 'x')), _ => "1.52", StringComparer.Ordinal);
        var request = new AnalyzeBizTalkSolutionRequest { SourcePath = Path.Combine(_directory, "Test.btproj"), AdditionalRulePaths = [Path.Combine(_directory, "rules.xml")], GenerateNativeFlow = native, NativePolicyVersions = targets };
        if (valid) Assert.Single((await new BizTalkImportService().AnalyzeAsync(request, default)).FlowCandidates);
        else Assert.Contains("Native policy targets", (await Assert.ThrowsAsync<ArgumentException>(() => new BizTalkImportService().AnalyzeAsync(request, default))).Message);
    }

    [Theory]
    [InlineData("missing-policy")]
    [InlineData("invalid-policy")]
    [InlineData("inactive-policy")]
    [InlineData("not-durable")]
    [InlineData("missing-message")]
    [InlineData("nested-behavior")]
    public async Task PolicyNodeRequiresActiveValidPolicyDurableNamedMessageAndLeafShape(string variation)
    {
        var (_, imported) = await Import();
        var node = imported.AllNodes().Single(node => node.Kind == NativeFlowNodeKind.ApplyXmlRules);
        var definition = new NativeFlowDefinition { Durable = true, Nodes = [node] };
        switch (variation)
        {
            case "missing-policy": node.PolicyJson = null; break;
            case "invalid-policy": node.PolicyJson = "{}"; break;
            case "inactive-policy": node.Kind = NativeFlowNodeKind.Sequence; break;
            case "not-durable": definition.Durable = false; break;
            case "missing-message": node.MessageName = ""; break;
            case "nested-behavior": node.Children.Add(new()); break;
        }
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json)));
    }

    [Fact]
    public async Task GraphicalNativePolicyEditorAndExplicitTargetPickerAreAvailableFromScratch()
    {
        var (candidate, definition) = await Import();
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost/") };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = JsonSerializer.Serialize(definition, Json) }));
            var html = rendered.ToHtmlString();
            Assert.Contains("ApplyXmlRules", html); Assert.Contains("xml-rule-policy-editor", html); Assert.Contains("Client A", html);
            Assert.DoesNotContain("Some nodes are incomplete or incompatible", html);
            var selected = definition.AllNodes().Single(node => node.Kind == NativeFlowNodeKind.ApplyXmlRules).PolicyJson!;
            var picker = await renderer.RenderComponentAsync<BizTalkRulePolicies>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Policies"] = new BizTalkRulePolicyViewModel[] { new("rules.xml", "Any Policy", 1, 52, 2, 2, [], selected, []) },
                ["EnableNativeTargets"] = true,
                ["OnSelectNativeTarget"] = EventCallback.Factory.Create<BizTalkRulePolicyViewModel>(new object(), _ => { })
            }));
            Assert.Contains("Use selected exported version as native import test target", picker.ToHtmlString());
        });
        var model = JsonSerializer.Deserialize<NativeFlowEditorModel>(candidate.DraftFlow.Augments.Single().Settings["flowJson"], Json)!;
        var node = model.Nodes.SelectMany(Flatten).Single(node => node.Kind == "ApplyXmlRules");
        Assert.NotNull(node.PolicyJson);
        Assert.Equal(node.PolicyJson, NativeFlowDefinition.Read(JsonSerializer.Serialize(model, Json)).AllNodes().Single(node => node.Kind == NativeFlowNodeKind.ApplyXmlRules).PolicyJson);
        var designer = new Mulse.Ui.Components.Pages.Designer();
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var targets = (Dictionary<string, string>)designer.GetType().GetField("_nativePolicyVersions", flags)!.GetValue(designer)!;
        targets["Any Policy"] = "1.52";
        designer.GetType().GetField("_nativeHostConfigurationPath", flags)!.SetValue(designer, "host.config");
        designer.GetType().GetField("_nativeConfigurationHelperPaths", flags)!.SetValue(designer, "A.cs; B.cs");
        var candidateModel = JsonSerializer.Deserialize<BizTalkFlowCandidateViewModel>(JsonSerializer.Serialize(candidate, Json), Json)!;
        var factory = designer.GetType().GetMethod("CreateBizTalkImportRequest", flags)!;
        var request = (CreateImportedBizTalkFlowRequestViewModel)factory.Invoke(designer, [candidateModel])!;
        Assert.Equal("1.52", Assert.Single(request.NativePolicyVersions!).Value);
        Assert.Equal("host.config", request.NativeHostConfigurationPath);
        Assert.Equal(new[] { "A.cs", "B.cs" }, request.NativeConfigurationHelperPaths);
        designer.GetType().GetField("_generateNativeBizTalkFlow", flags)!.SetValue(designer, false);
        var nonNative = (CreateImportedBizTalkFlowRequestViewModel)factory.Invoke(designer, [candidateModel])!;
        Assert.Null(nonNative.NativePolicyVersions); Assert.Null(nonNative.NativeHostConfigurationPath); Assert.Null(nonNative.NativeConfigurationHelperPaths);
        await (Task)typeof(NativeFlowNodesEditor).GetMethod("ChangeKind", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(new NativeFlowNodesEditor(), [node, "Sequence"])!;
        Assert.Null(node.PolicyJson);
        static IEnumerable<NativeFlowNodeViewModel> Flatten(NativeFlowNodeViewModel node) => new[] { node }.Concat(node.Children.SelectMany(Flatten)).Concat(node.Otherwise.SelectMany(Flatten));
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoEffects : IAugmentModuleInvoker, IRenderModuleInvoker
    {
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("Policy calls must not execute a helper or external module.");
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("Policy calls must not execute a helper or external module.");
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
