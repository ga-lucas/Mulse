using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
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

public sealed class NativeConfigurationMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-config-migration-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128, Converters = { new JsonStringEnumConverter() } };
    private const string Helper = """
        using System;
        namespace Example {
            public static class Settings {
                private const string SMTP = "SMTP Pattern";
                private const string FTP = "FTP Pattern";
                public static string Smtp() {
                    if (System.Configuration.ConfigurationManager.AppSettings[SMTP] != null)
                        return System.Configuration.ConfigurationManager.AppSettings[SMTP];
                    return string.Empty;
                }
                public static string Ftp() {
                    if (System.Configuration.ConfigurationManager.AppSettings[FTP] != null)
                        return System.Configuration.ConfigurationManager.AppSettings[FTP];
                    return string.Empty;
                }
            }
        }
        """;
    private string Write(string name, string text)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name); File.WriteAllText(path, text); return path;
    }
    private NativeSourceConfigurationCatalog Catalog(string config = "<configuration/>", string? helper = null)
        => NativeSourceConfigurationCatalog.Read(Write("host.config", config), [Write("Settings.cs", helper ?? Helper)]);
    private async Task<(BizTalkFlowCandidateResponse Candidate, NativeFlowDefinition Definition)> Import(bool selected = true, string suffix = "")
    {
        var project = Write("Example.btproj", "<Project><PropertyGroup><AssemblyName>Example</AssemblyName></PropertyGroup><ItemGroup><XLang Include='Flow.odx'/></ItemGroup></Project>");
        var config = Write("host.config", "<configuration><appSettings><add key='smtp pattern' value='  literal-pattern  '/></appSettings></configuration>");
        var helper = Write("Settings.cs", Helper);
        XNamespace om = "http://schemas.microsoft.com/BizTalk/2003/DesignerData";
        XElement P(string key, string value) => new(om + "Property", new XAttribute("Name", key), new XAttribute("Value", value));
        XElement N(string kind, string name, params object[] children) => new(om + "Element", new XAttribute("Type", kind), P("Name", name), children);
        var source = new XElement(om + "MetaModel", N("ServiceDeclaration", "Flow",
            new[] { "smtp", "ftp", "before", "after" }.Select(name => N("VariableDeclaration", name, P("Type", "System.String"))),
            N("MessageDeclaration", "request", P("Type", "System.Xml.XmlDocument")),
            N("ServiceBody", "Body", N("Receive", "input", P("MessageName", "request"), P("PortName", "in")),
                N("Expression", "initialize", P("Expression", "smtp = Example.Settings.Smtp(); ftp = Example.Settings.Ftp(); before = System.Convert.ToString(Microsoft.XLANGs.Core.Service.RootService.InstanceId);" + suffix)),
                N("Expression", "read-again", P("Expression", "after = System.Convert.ToString(Microsoft.XLANGs.Core.Service.RootService.InstanceId);")),
                N("Send", "reply", P("MessageName", "request"), P("OperationMessageName", "Response")))));
        Write("Flow.odx", "<?xml version='1.0'?>" + source);
        var candidate = Assert.Single((await new BizTalkImportService().AnalyzeAsync(new()
        {
            SourcePath = project, GenerateNativeFlow = true, NativeHostConfigurationPath = selected ? config : null,
            NativeConfigurationHelperPaths = selected ? [helper] : []
        }, default)).FlowCandidates);
        return (candidate, NativeFlowDefinition.Read(candidate.DraftFlow.Augments.Single().Settings["flowJson"], allowBlocked: true));
    }

    [Theory]
    [InlineData("<configuration/>", "")]
    [InlineData("<configuration><appSettings/></configuration>", "")]
    [InlineData("<configuration><appSettings><add key='SMTP Pattern' value=''/></appSettings></configuration>", "")]
    [InlineData("<configuration><appSettings><add key='smtp pattern' value='  x  '/></appSettings></configuration>", "  x  ")]
    public void MissingKeysReturnProvenEmptyStringAndConfiguredValuesPreserveWhitespace(string config, string expected)
    {
        var catalog = Catalog(config);
        Assert.True(catalog.TryResolve("Example.Settings.Smtp", out var value)); Assert.Equal(expected, value);
        Assert.True(catalog.TryResolve("Example.Settings.Ftp", out value)); Assert.Equal("", value);
        Assert.False(catalog.TryResolve("example.Settings.Smtp", out _));
        Assert.False(catalog.TryResolve("Example.Settings.Password", out _));
    }

    [Theory]
    [InlineData("<configuration><appSettings file='external.config'/></configuration>")]
    [InlineData("<configuration><appSettings configSource='external.config'/></configuration>")]
    [InlineData("<configuration><appSettings/><appSettings/></configuration>")]
    [InlineData("<configuration><appSettings><add key='X' value='a'/><add key='x' value='b'/></appSettings></configuration>")]
    [InlineData("<configuration><appSettings><remove key='X'/></appSettings></configuration>")]
    [InlineData("<configuration><appSettings><clear/></appSettings></configuration>")]
    [InlineData("<configuration><location path='a'><appSettings/></location></configuration>")]
    [InlineData("<configuration><xlangs><ConfigurationFile>external.config</ConfigurationFile></xlangs></configuration>")]
    [InlineData("<!DOCTYPE configuration SYSTEM 'file:///not-read'><configuration/>")]
    [InlineData("<configuration xmlns='urn:unknown'/>")]
    public void AmbiguousExternalOrUnsupportedConfigurationNeverBecomesAnEmptySuccess(string config)
        => Assert.Throws<ArgumentException>(() => Catalog(config));

    [Theory]
    [InlineData("static-constructor")]
    [InlineData("suffix")]
    [InlineData("changed-key")]
    [InlineData("changed-fallback")]
    [InlineData("preprocessor")]
    [InlineData("partial")]
    public void WholeHelperSourceMustProveOnlySupportedPureGetters(string variation)
    {
        var helper = variation switch
        {
            "static-constructor" => Helper.Replace("private const", "static Settings() { SideEffect(); } private const"),
            "suffix" => Helper + " class Other {}",
            "changed-key" => Helper.Replace("return System.Configuration.ConfigurationManager.AppSettings[SMTP]", "return System.Configuration.ConfigurationManager.AppSettings[FTP]"),
            "changed-fallback" => Helper.Replace("return string.Empty;", "return \"guessed\";"),
            "preprocessor" => "#define SOMETHING\n" + Helper,
            _ => Helper.Replace("public static class", "public static partial class")
        };
        Assert.Throws<ArgumentException>(() => Catalog(helper: helper));
    }

    [Fact]
    public void DuplicateSelectedHelperIdentitiesAreNotResolvedByOrder()
    {
        var config = Write("host.config", "<configuration/>");
        Assert.Throws<ArgumentException>(() => NativeSourceConfigurationCatalog.Read(config, [Write("A.cs", Helper), Write("B.cs", Helper)]));
        Assert.Throws<ArgumentException>(() => Catalog(helper: Helper + new string(' ', 65_536)));
        Assert.Throws<ArgumentException>(() => Catalog("<configuration><appSettings><add key='X' value='" + new string('x', 4097) + "'/></appSettings></configuration>"));
    }

    [Theory]
    [InlineData(16, true, true)]
    [InlineData(17, true, false)]
    [InlineData(1, false, false)]
    [InlineData(0, true, false)]
    public async Task SnapshotRequiresPairedExplicitPathsNativeGenerationAndBoundedHelperCount(int count, bool native, bool valid)
    {
        await Import();
        var helper = Path.Combine(_directory, "Settings.cs");
        var request = new AnalyzeBizTalkSolutionRequest { SourcePath = Path.Combine(_directory, "Example.btproj"), GenerateNativeFlow = native,
            NativeHostConfigurationPath = Path.Combine(_directory, "host.config"), NativeConfigurationHelperPaths = Enumerable.Repeat(helper, count).ToArray() };
        if (valid) Assert.Single((await new BizTalkImportService().AnalyzeAsync(request, default)).FlowCandidates);
        else Assert.Contains("Native configuration snapshots", (await Assert.ThrowsAsync<ArgumentException>(() => new BizTalkImportService().AnalyzeAsync(request, default))).Message);
    }

    [Fact]
    public void MaximumPinnedValueAndHelperSourceBoundsAreExact()
    {
        var catalog = Catalog("<configuration><appSettings><add key='SMTP Pattern' value='" + new string('x', 4096) + "'/></appSettings></configuration>");
        Assert.True(catalog.TryResolve("Example.Settings.Smtp", out var value)); Assert.Equal(4096, value.Length);
        Catalog(helper: Helper + new string(' ', 65_536 - Helper.Length));
        Assert.Throws<ArgumentException>(() => Catalog(helper: Helper + new string(' ', 65_537 - Helper.Length)));
    }

    [Fact]
    public async Task CompleteImportedInitializationUsesPinnedValuesAndStableNativeIdentityAcrossDiskRestart()
    {
        var (candidate, definition) = await Import();
        Assert.False(candidate.DraftFlow.Enabled);
        Assert.DoesNotContain(definition.AllNodes(), node => node.Kind == NativeFlowNodeKind.Blocked);
        Assert.Contains(candidate.DraftFlow.Warnings, warning => warning.Contains("not proof of deployed assembly identity"));
        var initialization = definition.AllNodes().Single(node => node.Name == "initialize");
        Assert.Equal(3, initialization.Children.Count);
        Assert.Equal("  literal-pattern  ", initialization.Children[0].Expression!.Literal!.Value);
        Assert.Equal("", initialization.Children[1].Expression!.Literal!.Value);
        Assert.Equal(NativeExpressionKind.InstanceId, initialization.Children[2].Expression!.Kind);
        var body = definition.AllNodes().Single(node => node.Name == "Body");
        body.Children.Insert(2, new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 1 });
        body.Children.Insert(body.Children.Count - 1, new() { Kind = NativeFlowNodeKind.SetMessageValue,
            Target = new() { MessageName = "request", Kind = NativeMessageFactKind.Metadata, MetadataKey = "native-id" }, Expression = new() { Kind = NativeExpressionKind.InstanceId } });
        var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, Json) } };
        File.Delete(Path.Combine(_directory, "Settings.cs")); File.Delete(Path.Combine(_directory, "host.config"));
        var clock = new Clock();
        IFlowOrchestrationStateStore Store() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }), new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance));
        var input = new IntegrationBatch([new("request.xml", BinaryData.FromString("<r/>"), "application/xml", new Dictionary<string, string> { ["group"] = "A", ["messageId"] = "1", ["channel"] = "in" })]);
        var context = new FlowExecutionContext("config", "first", clock.Now);
        await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(context, input, step, default);
        var saved = Assert.Single(await Store().GetAllAsync("config", default));
        var identity = Guid.ParseExact(saved.InstanceId, "N").ToString("D");
        Assert.Equal(identity, saved.Native!.State.Variables["before"].Value);
        Assert.Equal("  literal-pattern  ", saved.Native.State.Variables["smtp"].Value);
        Assert.Equal("", saved.Native.State.Variables["ftp"].Value);
        clock.Now += TimeSpan.FromSeconds(1);
        var resumed = new FlowExecutionContext("config", "second", clock.Now);
        resumed.SetOrchestrationInstance(saved.InstanceId, saved.CorrelationKey, saved.Bookmark, true);
        await new NativeControlFlowAugmentModule(new NoEffects(), new NoEffects(), Store(), clock).AugmentAsync(resumed, IntegrationBatch.Empty, step, default);
        Assert.Equal(identity, Assert.Single(resumed.CapturedResponses).Metadata["native-id"]);
        Assert.Equal(identity, Assert.Single(await Store().GetAllAsync("config", default)).Native!.State.Variables["after"].Value);
        Assert.False(input.Payloads[0].Metadata.ContainsKey("native-id"));
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(true, " Untranslated.Helper();")]
    public async Task UnselectedGettersAndUnsupportedSuffixesKeepTheEntireStatementBlocked(bool selected, string suffix)
    {
        var (_, definition) = await Import(selected, suffix);
        var initialize = definition.AllNodes().Single(node => node.Name == "initialize");
        Assert.Equal(NativeFlowNodeKind.Blocked, initialize.Kind); Assert.Empty(initialize.Children);
    }

    [Fact]
    public async Task InstanceIdentityIsGraphicallyAuthorableAndRequiresDurableExecution()
    {
        var expression = new NativeExpression { Kind = NativeExpressionKind.InstanceId };
        NativeExpression.Validate(expression);
        Assert.Throws<InvalidOperationException>(() => expression.Evaluate(new Dictionary<string, NativeScalar>()));
        var definition = new NativeFlowDefinition { Nodes = [new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "id", VariableType = NativeScalarType.String, Expression = expression }] };
        Assert.Throws<ArgumentException>(() => NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json)));
        definition.Durable = true;
        NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json));
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var view = JsonSerializer.Deserialize<NativeFlowEditorModel>(JsonSerializer.Serialize(definition, Json), Json)!;
            var rendered = await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = JsonSerializer.Serialize(view, Json) }));
            Assert.Contains("Stable native durable instance GUID", rendered.ToHtmlString());
            Assert.DoesNotContain("Some nodes are incomplete or incompatible", rendered.ToHtmlString());
        });
        var model = new NativeExpressionViewModel();
        var editor = new NativeExpressionEditor();
        typeof(NativeExpressionEditor).GetProperty("Value")!.SetValue(editor, model);
        await (Task)typeof(NativeExpressionEditor).GetMethod("ChangeKind", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(editor, [new ChangeEventArgs { Value = "InstanceId" }])!;
        Assert.Null(model.Literal); Assert.Null(model.Fact); Assert.Null(model.Variable); Assert.Empty(model.Operands);
    }

    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero); public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoEffects : IAugmentModuleInvoker, IRenderModuleInvoker
    {
        public Task<IntegrationBatch> AugmentAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No helper execution allowed.");
        public Task<IntegrationBatch> RenderAsync(string id, FlowExecutionContext context, IntegrationBatch batch, IReadOnlyDictionary<string, string> settings, CancellationToken token) => throw new InvalidOperationException("No helper execution allowed.");
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
