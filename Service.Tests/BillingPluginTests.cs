using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Billing;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Modules.ControlFlow;
using Mulse.Modules.Execution;
using Mulse.Modules.Mapping;
using Mulse.Modules.Payloads;
using Mulse.Modules.Pipeline;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Service.Models;

namespace Service.Tests;

public sealed class BillingPluginTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-billing-plugin-" + Guid.NewGuid().ToString("N"));
    private readonly List<WeakReference> _unloadingContexts = [];
    private static readonly DateTimeOffset Instant = new(2026, 11, 5, 23, 45, 12, TimeSpan.Zero);
    private static readonly Guid Identity = Guid.Parse("2ae6889b-0c99-491b-898d-ebc55ef9ab1d");
    private static Dictionary<string, string> EnvironmentSettings() => new() { ["timeZone"] = "UTC", ["culture"] = "Invariant" };
    private static FlowExecutionContext Context() => new("billing", Guid.NewGuid().ToString("N"), Instant);
    private static IntegrationBatch Input(string body = "<r />") => new([new("billing.xml", BinaryData.FromString(body), "application/xml", new Dictionary<string, string>
        { ["group"] = "A", ["messageId"] = "1", ["channel"] = "input", ["keep"] = "original" })]);
    private static string Expand(string template) => BillingFilename.Expand(template, Instant, TimeZoneInfo.Utc, CultureInfo.InvariantCulture, Identity);

    [Theory]
    [InlineData("[DATE(yyyyMMdd)]", "20261105")]
    [InlineData("[DATETIME(yyyyMMdd_HHmmss)]", "20261105_234512")]
    [InlineData("[UTCDATETIME(yyyyMMdd_HHmmss)]", "20261105_234512")]
    [InlineData("[TIME(HHmm)]_[UTCTIME(HHmm)]", "2345_2345")]
    [InlineData("[DAY(0)]_[DAY(3)]_[DAY(7)]", "THURSDAY_THU_THURSDAY")]
    [InlineData("[GUID]_[GUID]", "2ae6889b-0c99-491b-898d-ebc55ef9ab1d_2ae6889b-0c99-491b-898d-ebc55ef9ab1d")]
    [InlineData("folder\\file.txt", "folder\\file.txt")]
    [InlineData("[datetime(yyyy)]", "[datetime(yyyy)]")]
    public void FilenameMacrosHaveExplicitSourceCompatibleSemantics(string template, string expected) => Assert.Equal(expected, Expand(template));

    [Fact]
    public void FilenameUsesExplicitZoneCultureAndSourceSecondsEpoch()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("SyntheticPlus2", TimeSpan.FromHours(2), "SyntheticPlus2", "SyntheticPlus2");
        Assert.Equal("20261106_0145_20261105_2345", BillingFilename.Expand("[DATETIME(yyyyMMdd_HHmm)]_[UTCDATETIME(yyyyMMdd_HHmm)]", Instant, zone, CultureInfo.InvariantCulture, Identity));
        Assert.Equal(Instant.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture), Expand("[TICKS]"));
        Assert.Equal((Instant.UtcDateTime - new DateTime(2001, 1, 1)).TotalSeconds.ToString("0", CultureInfo.InvariantCulture), Expand("[SECONDS]"));
        Assert.Equal("novembre", BillingFilename.Expand("[DATE(MMMM)]", Instant, TimeZoneInfo.Utc, CultureInfo.GetCultureInfo("fr-FR"), Identity));
    }

    [Theory]
    [InlineData("[DAY(no)]")]
    [InlineData("[DAY(-1)]")]
    [InlineData("[DATE(yyyy/MM/dd)]")]
    [InlineData("[GUID(bad)]")]
    [InlineData("[DATE(yyyy)")]
    public void InvalidKnownFilenameTokensFailInsteadOfProducingSuccessShapedPlaceholders(string template)
        => Assert.Throws<ArgumentException>(() => Expand(template));

    [Fact]
    public async Task FilenameBoundariesInputTypesAndCancellationFailExplicitly()
    {
        Assert.Equal(4096, Expand(new string('x', 4096)).Length);
        Assert.Throws<ArgumentException>(() => Expand(new string('x', 4097)));
        Assert.Throws<ArgumentException>(() => Expand(new string('x', 4090) + "[GUID]"));
        var module = new BillingFilenameTransformModule();
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [JsonValue.Create(12)], new() { Settings = EnvironmentSettings() }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [], new() { Settings = EnvironmentSettings() }, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.InvokeAsync(Context(), [JsonValue.Create("[DATE(yyyy)]")], new(), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.InvokeAsync(Context(), [JsonValue.Create("x")], new(), new CancellationToken(true)));
    }

    [Theory]
    [InlineData("CaseID", "Details", "CaseId")]
    [InlineData("ScanListID", "Details", "ScanListId")]
    [InlineData("EncounterID", "Details", "EncounterId")]
    [InlineData("TargetLocation", "ConfigurationInfo", "TargetLocation")]
    [InlineData("GroupID", "ConfigurationInfo", "GroupID")]
    [InlineData("UpdateMethod", "Details", "UpdateMethod")]
    [InlineData("CaseNumber", "Details", "CaseNumber")]
    [InlineData("DepartmentID", "ConfigurationInfo", "DepartmentID")]
    [InlineData("DepartmentName", "ConfigurationInfo", "DepartmentName")]
    [InlineData("TransportType", "ConfigurationInfo", "TransportType")]
    [InlineData("BatchTime", "ConfigurationInfo", "BatchTime")]
    [InlineData("BatchGroup", "ConfigurationInfo", "BatchGroup")]
    [InlineData("IsBatch", "ConfigurationInfo", "IsBatch")]
    public async Task CommonBillingFieldsUseSourceSchemaNamesNotCaseOrNamespaceHeuristics(string field, string section, string element)
    {
        var input = Input($"<b:Billing xmlns:b='{BillingFieldAugmentModule.BillingNamespace}'><{section}><{element}>value</{element}></{section}></b:Billing>");
        var module = new BillingFieldAugmentModule();
        var step = new ModuleStepDefinition { Settings = new() { ["field"] = field, ["outputKey"] = "chosen" } };
        var output = Assert.Single((await module.AugmentAsync(Context(), input, step, default)).Payloads);
        Assert.Equal("value", output.Metadata["chosen"]); Assert.Equal("original", output.Metadata["keep"]);
        Assert.Equal(input.Payloads[0].Content, output.Content); Assert.Equal(input.Payloads[0].Name, output.Name);
        Assert.False(input.Payloads[0].Metadata.ContainsKey("chosen"));
    }

    [Theory]
    [InlineData("<Billing><ConfigurationInfo><TargetLocation>x</TargetLocation></ConfigurationInfo></Billing>")]
    [InlineData("<b:Billing xmlns:b='{0}'><ConfigurationInfo/></b:Billing>")]
    [InlineData("<Billing xmlns='{0}'><ConfigurationInfo><TargetLocation>x</TargetLocation></ConfigurationInfo></Billing>")]
    [InlineData("<b:Billing xmlns:b='{0}'><ConfigurationInfo><targetLocation>x</targetLocation></ConfigurationInfo></b:Billing>")]
    [InlineData("<b:Billing xmlns:b='{0}'><ConfigurationInfo><TargetLocation xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:nil='true'/></ConfigurationInfo></b:Billing>")]
    [InlineData("<b:Billing xmlns:b='{0}'><ConfigurationInfo><TargetLocation>x</TargetLocation><TargetLocation>y</TargetLocation></ConfigurationInfo></b:Billing>")]
    [InlineData("<b:Billing xmlns:b='{0}'><ConfigurationInfo><TargetLocation><nested>x</nested></TargetLocation></ConfigurationInfo></b:Billing>")]
    [InlineData("<b:Billing xmlns:b='{0}'><ConfigurationInfo/><ConfigurationInfo><TargetLocation>x</TargetLocation></ConfigurationInfo></b:Billing>")]
    [InlineData("<!DOCTYPE r [<!ENTITY x 'unsafe'>]><r>&x;</r>")]
    public async Task CommonBillingInvalidMissingDuplicateOrUnsafeFieldsFail(string body)
    {
        var module = new BillingFieldAugmentModule();
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(Context(), Input(string.Format(CultureInfo.InvariantCulture, body, BillingFieldAugmentModule.BillingNamespace)),
            new() { Settings = new() { ["field"] = "TargetLocation", ["outputKey"] = "target" } }, default));
    }

    [Fact]
    public async Task CommonBillingDoesNotPromoteCredentialsAndFilenameAugmentPreservesContent()
    {
        var promotion = new BillingFieldAugmentModule();
        await Assert.ThrowsAsync<ArgumentException>(() => promotion.AugmentAsync(Context(), Input(), new() { Settings = new() { ["field"] = "Password", ["outputKey"] = "password" } }, default));
        Assert.DoesNotContain(promotion.Descriptor.Settings[0].Options!, option => option.Value is "Password" or "UserName" or "CertificateName");
        var settings = EnvironmentSettings(); settings["source"] = "Metadata"; settings["template"] = "source"; settings["outputKey"] = "target";
        var payload = Input().Payloads[0];
        var metadata = new Dictionary<string, string>(payload.Metadata) { ["source"] = "file_[DATE(yyyyMMdd)].xml" };
        var original = payload with { Metadata = metadata };
        var module = new BillingFilenameAugmentModule();
        var result = Assert.Single((await module.AugmentAsync(Context(), new([original]), new() { Settings = settings }, default)).Payloads);
        Assert.Equal("file_20261105.xml", result.Metadata["target"]); Assert.Equal(original.Content, result.Content); Assert.Equal(original.Name, result.Name);
        Assert.False(original.Metadata.ContainsKey("target"));
        settings["template"] = "Source";
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(Context(), new([original]), new() { Settings = settings }, default));
    }

    [Fact]
    public async Task NamespaceRemovalPreservesValuesNodesAndMetadataButRejectsCollisions()
    {
        var module = new BillingRemoveNamespacesRenderModule();
        const string xml = "<?before keep?><a:r xmlns:a='urn:a' xmlns:b='urn:b' b:id='7'> before <b:i><![CDATA[value]]></b:i><!--keep--> after </a:r>";
        var input = Input(xml);
        var output = Assert.Single((await module.RenderAsync(Context(), input, new(), default)).Payloads);
        var document = XDocument.Parse(output.GetText(), LoadOptions.PreserveWhitespace);
        Assert.Equal("r", document.Root!.Name); Assert.Equal("7", document.Root.Attribute("id")!.Value);
        Assert.Equal(" before value after ", document.Root.Value);
        Assert.Contains("<?before keep?>", output.GetText()); Assert.Contains("<![CDATA[value]]>", output.GetText()); Assert.Contains("<!--keep-->", output.GetText());
        Assert.All(document.Descendants(), element => Assert.Empty(element.Name.NamespaceName));
        Assert.Equal("original", output.Metadata["keep"]); Assert.Equal(xml, input.Payloads[0].GetText());
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context(), Input("<r xmlns:a='urn:a' id='a' a:id='b'/>"), new(), default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context(), Input("<!DOCTYPE r [<!ENTITY x 'unsafe'>]><r>&x;</r>"), new(), default));
    }

    [Fact]
    public async Task EmptyBatchesStillValidateFilenameConfigurationAndModulesHonorCancellation()
    {
        var module = new BillingFilenameAugmentModule();
        var settings = EnvironmentSettings(); settings["source"] = "Literal"; settings["template"] = "file.txt"; settings["outputKey"] = "target";
        Assert.Empty((await module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = settings }, default)).Payloads);
        settings["culture"] = "not a culture";
        await Assert.ThrowsAsync<CultureNotFoundException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = settings }, default));
        settings["culture"] = "Invariant"; settings["timeZone"] = "Mulse-not-an-installed-zone";
        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new() { Settings = settings }, default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.AugmentAsync(Context(), IntegrationBatch.Empty, new(), new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BillingFieldAugmentModule().AugmentAsync(Context(), IntegrationBatch.Empty, new(), new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BillingRemoveNamespacesRenderModule().RenderAsync(Context(), IntegrationBatch.Empty, new(), new CancellationToken(true)));
    }

    [Fact]
    public async Task BillingPluginLoadsThroughRealCatalogAndWorksInMapperNativeFlowAndUi()
    {
        Directory.CreateDirectory(_directory);
        var plugins = Path.Combine(_directory, "plugins", "Mulse.Billing"); Directory.CreateDirectory(plugins);
        var assembly = Path.Combine(plugins, "Mulse.Billing.dll"); File.Copy(typeof(BillingModuleInstaller).Assembly.Location, assembly);
        var environment = new FakeHostEnvironment { ContentRootPath = _directory };
        var store = new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json"), PluginDirectories = ["plugins"] }), environment, NoOpRuntimeStatePayloadProtector.Instance);
        using var services = new ServiceCollection().AddLogging().AddMulsePlatform(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), _directory)
            .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(environment).AddSingleton<IRuntimeConfigurationStore>(store).BuildServiceProvider();
        var catalog = new ModuleCatalog(services, store, environment, TimeProvider.System, NullLogger<ModuleCatalog>.Instance);
        await catalog.SynchronizeAsync(default);
        Assert.False(catalog.GetPackages().Single(package => package.Id == "Mulse.Billing").IsLoaded);
        await catalog.UpsertManagedPackageAsync(new() { Id = "Mulse.Billing", AssemblyPath = assembly, Enabled = true }, default);
        try
        {
            RecordTransformContext(catalog);
            Assert.Equal(4, catalog.GetPackages().Single(package => package.Id == "Mulse.Billing").ModuleCount);
            using var invokers = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
                .AddSingleton<IAugmentModuleInvoker, Service.Modules.AugmentModuleInvoker>()
                .AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>()
                .AddSingleton<IModuleInvoker, Service.Modules.ModuleInvoker>().BuildServiceProvider();
            const string mapping = """
                {"fields":[{"kind":"Transform","targetPath":"/filename","transformModule":"billing-filename-transform",
                "transformSettings":{"timeZone":"UTC","culture":"Invariant"},"transformInputs":[{"sourcePath":"/template"}]}]}
                """;
            Assert.Equal("[{\"filename\":\"bill_20261105.txt\"}]", await ParsedDataMapping.MapAsync(mapping, "{\"template\":\"bill_[DATE(yyyyMMdd)].txt\"}", "Json", default, invokers.GetRequiredService<IModuleInvoker>(), Context()));
            await catalog.ReloadPackageAsync("Mulse.Billing", default);
            RecordTransformContext(catalog);
            Assert.Equal("[{\"filename\":\"reloaded_2026\"}]", await ParsedDataMapping.MapAsync(mapping, "{\"template\":\"reloaded_[DATE(yyyy)]\"}", "Json", default, invokers.GetRequiredService<IModuleInvoker>(), Context()));
            var augments = invokers.GetRequiredService<IAugmentModuleInvoker>(); var renderers = invokers.GetRequiredService<IRenderModuleInvoker>();
            var settings = EnvironmentSettings(); settings["source"] = "Metadata"; settings["template"] = "billing.TargetLocation"; settings["outputKey"] = "expanded";
            var definition = new NativeFlowDefinition { Durable = true, Nodes = [
                new() { Kind = NativeFlowNodeKind.Augment, Module = "billing-field-augment", Settings = new() { ["field"] = "TargetLocation", ["outputKey"] = "billing.TargetLocation" } },
                new() { Kind = NativeFlowNodeKind.Augment, Module = "billing-filename-augment", Settings = settings },
                new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5 },
                new() { Kind = NativeFlowNodeKind.Render, Module = "billing-remove-namespaces-render" }
            ] };
            var step = new ModuleStepDefinition { Module = NativeFlowDefinition.ModuleId, Settings = new() { ["flowJson"] = JsonSerializer.Serialize(definition, JsonSerializerOptions.Web) } };
            var clock = new Clock();
            IFlowOrchestrationStateStore State() => new FlowOrchestrationStateStore(new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "native.json") }), environment, NoOpRuntimeStatePayloadProtector.Instance));
            var first = Context();
            await new NativeControlFlowAugmentModule(augments, renderers, State(), clock).AugmentAsync(first,
                Input($"<b:Billing xmlns:b='{BillingFieldAugmentModule.BillingNamespace}'><ConfigurationInfo><TargetLocation>bill_[GUID].txt</TargetLocation></ConfigurationInfo></b:Billing>"), step, default);
            var checkpoint = Assert.Single(await State().GetAllAsync("billing", default));
            var expanded = checkpoint.Payloads.Single().Restore().Metadata["expanded"];
            Assert.True(Guid.TryParse(expanded[5..^4], out _));
            clock.Now = clock.Now.AddSeconds(5);
            var resumed = Context(); resumed.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, checkpoint.Bookmark, true);
            var result = await new NativeControlFlowAugmentModule(augments, renderers, State(), clock).AugmentAsync(resumed, IntegrationBatch.Empty, step, default);
            Assert.Equal(expanded, result.Payloads.Single().Metadata["expanded"]);
            Assert.Empty(XDocument.Parse(result.Payloads.Single().GetText()).Root!.Name.NamespaceName);
            Assert.Equal("Completed", Assert.Single(await State().GetAllAsync("billing", default)).Native!.State.Status);
            await store.UpsertFlowAsync(new() { Id = "billing-helper-test", Enabled = false, Augments = [step] }, default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpsertManagedPackageAsync(new() { Id = "Mulse.Billing", AssemblyPath = assembly, Enabled = false }, default));
            var moduleViews = catalog.GetAll().Where(entry => entry.PackageId == "Mulse.Billing").Select(entry => View(entry.Descriptor)).ToArray();
            var html = await Render<NativeFlowEditor>(new() { ["Value"] = step.Settings["flowJson"], ["Modules"] = moduleViews });
            Assert.Contains("Filename time zone", html); Assert.Contains("CommonBilling field", html); Assert.Contains("TargetLocation", html);
            Assert.DoesNotContain("Original JSON is preserved", html);
            var descriptor = View(new BillingFilenameTransformModule().Descriptor);
            var fields = await Render<ModuleSettingsFields>(new() { ["Descriptor"] = descriptor, ["Settings"] = EnvironmentSettings() });
            Assert.Contains("Filename culture", fields); Assert.Contains("Filename time zone", fields);
        }
        finally
        {
            await store.DeleteFlowAsync("billing-helper-test", default);
            await catalog.UpsertManagedPackageAsync(new() { Id = "Mulse.Billing", AssemblyPath = assembly, Enabled = false }, default);
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private void RecordTransformContext(IModuleCatalog catalog)
    {
        var lease = catalog.LeaseTransformAsync("billing-filename-transform", default).GetAwaiter().GetResult();
        try
        {
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(lease.Module.GetType().Assembly)!;
            Assert.True(context.IsCollectible);
            _unloadingContexts.Add(new WeakReference(context));
        }
        finally { lease.DisposeAsync().GetAwaiter().GetResult(); }
    }

    private static ModuleViewModel View(ModuleDescriptor descriptor) => new(descriptor.Id, descriptor.DisplayName, descriptor.Kind.ToString(), descriptor.Description,
        descriptor.Settings.Select(setting => new ModuleSettingViewModel(setting.Key, setting.Label, setting.Description, setting.IsRequired, setting.InputKind.ToString(), setting.DefaultValue,
            setting.Options?.Select(option => new ModuleSettingOptionViewModel(option.Value, option.Label)).ToArray() ?? [])).ToArray(), [], [], []);
    private static async Task<string> Render<T>(Dictionary<string, object?> parameters) where T : IComponent
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<T>(ParameterView.FromDictionary(parameters))).ToHtmlString());
    }
    private sealed class Clock : TimeProvider { public DateTimeOffset Now { get; set; } = Instant; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
    public void Dispose()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            if (attempt > 0 && _unloadingContexts.All(context => !context.IsAlive)) break;
            Thread.Sleep(20);
        }
        Assert.All(_unloadingContexts, context => Assert.False(context.IsAlive, "The unloaded Billing plugin context is still rooted."));
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
