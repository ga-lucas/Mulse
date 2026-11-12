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
using Mulse.CompatibilityPack;
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
    private static Dictionary<string, string> EnvironmentSettings() => new() { ["timeZone"] = "UTC", ["culture"] = "Invariant" };
    private static FlowExecutionContext Context() => new("billing", Guid.NewGuid().ToString("N"), Instant);
    private static IntegrationBatch Input(string body = "<r />") => new([new("billing.xml", BinaryData.FromString(body), "application/xml", new Dictionary<string, string>
        { ["group"] = "A", ["messageId"] = "1", ["channel"] = "input", ["keep"] = "original" })]);

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
    public async Task CommonBillingDoesNotPromoteCredentials()
    {
        var promotion = new BillingFieldAugmentModule();
        await Assert.ThrowsAsync<ArgumentException>(() => promotion.AugmentAsync(Context(), Input(), new() { Settings = new() { ["field"] = "Password", ["outputKey"] = "password" } }, default));
        Assert.DoesNotContain(promotion.Descriptor.Settings[0].Options!, option => option.Value is "Password" or "UserName" or "CertificateName");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => promotion.AugmentAsync(Context(), IntegrationBatch.Empty, new(), new CancellationToken(true)));
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
            var originalShadowAssembly = RecordPluginContext(catalog);
            Assert.Equal(1, catalog.GetPackages().Single(package => package.Id == "Mulse.Billing").ModuleCount);
            using var invokers = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
                .AddSingleton<IAugmentModuleInvoker, Service.Modules.AugmentModuleInvoker>()
                .AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>()
                .AddSingleton<IModuleInvoker, Service.Modules.ModuleInvoker>().BuildServiceProvider();
            const string mapping = """
                {"fields":[{"kind":"Transform","targetPath":"/filename","transformModule":"filename-template-transform",
                "transformSettings":{"timeZone":"UTC","culture":"Invariant"},"transformInputs":[{"sourcePath":"/template"}]}]}
                """;
            Assert.Equal("[{\"filename\":\"bill_20261105.txt\"}]", await ParsedDataMapping.MapAsync(mapping, "{\"template\":\"bill_[DATE(yyyyMMdd)].txt\"}", "Json", default, invokers.GetRequiredService<IModuleInvoker>(), Context()));
            File.SetLastWriteTimeUtc(assembly, File.GetLastWriteTimeUtc(assembly).AddSeconds(2));
            await catalog.ReloadPackageAsync("Mulse.Billing", default);
            Assert.NotEqual(originalShadowAssembly, RecordPluginContext(catalog));
            Assert.Equal("[{\"filename\":\"reloaded_2026\"}]", await ParsedDataMapping.MapAsync(mapping, "{\"template\":\"reloaded_[DATE(yyyy)]\"}", "Json", default, invokers.GetRequiredService<IModuleInvoker>(), Context()));
            var augments = invokers.GetRequiredService<IAugmentModuleInvoker>(); var renderers = invokers.GetRequiredService<IRenderModuleInvoker>();
            var settings = EnvironmentSettings(); settings["source"] = "Metadata"; settings["template"] = "billing.TargetLocation"; settings["outputKey"] = "expanded";
            var definition = new NativeFlowDefinition { Durable = true, Nodes = [
                new() { Kind = NativeFlowNodeKind.Augment, Module = "billing-field-augment", Settings = new() { ["field"] = "TargetLocation", ["outputKey"] = "billing.TargetLocation" } },
                new() { Kind = NativeFlowNodeKind.Augment, Module = "filename-template-augment", Settings = settings },
                new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 5 },
                new() { Kind = NativeFlowNodeKind.Render, Module = "xml-remove-namespaces-render" }
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
            var projected = await augments.AugmentAsync("billing-field-augment", Context(), Input($"<b:Billing xmlns:b='{BillingFieldAugmentModule.BillingNamespace}'><ConfigurationInfo><IsBatch>1</IsBatch></ConfigurationInfo></b:Billing>"),
                new Dictionary<string, string> { ["field"] = "IsBatch", ["outputIdentity"] = "SourceProperty" }, default);
            Assert.True((bool)new NativeMessageFact { Kind = NativeMessageFactKind.MetadataBoolean, MetadataKey = "{http://Shs.SpaceTrax2.Interfaces.Core.Schemas.Properties.CoreProperties}IsBatch" }.Read(projected).Read()!);
            await store.UpsertFlowAsync(new() { Id = "billing-helper-test", Enabled = false, Augments = [step] }, default);
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.UpsertManagedPackageAsync(new() { Id = "Mulse.Billing", AssemblyPath = assembly, Enabled = false }, default));
            var moduleViews = catalog.GetAll().Where(entry => entry.PackageId is "Mulse.Billing" or "builtin.compatibility").Select(entry => View(entry.Descriptor)).ToArray();
            var html = await Render<NativeFlowEditor>(new() { ["Value"] = step.Settings["flowJson"], ["Modules"] = moduleViews });
            Assert.Contains("Filename time zone", html); Assert.Contains("CommonBilling field", html); Assert.Contains("TargetLocation", html);
            Assert.Contains("Declared Billing context property", html); Assert.Contains("SourceProperty", html);
            Assert.DoesNotContain("Original JSON is preserved", html);
            var descriptor = View(new FilenameTemplateTransformModule().Descriptor);
            var fields = await Render<ModuleSettingsFields>(new() { ["Descriptor"] = descriptor, ["Settings"] = EnvironmentSettings() });
            Assert.Contains("Filename culture", fields); Assert.Contains("Filename time zone", fields);
        }
        finally
        {
            await store.DeleteFlowAsync("billing-helper-test", default);
            await catalog.UpsertManagedPackageAsync(new() { Id = "Mulse.Billing", AssemblyPath = assembly, Enabled = false }, default);
        }
    }

    [Fact]
    public async Task NewMigrationModulesExposeExplicitLabeledFieldsInTheSettingsUi()
    {
        var cases = new (ModuleDescriptor Descriptor, string[] Labels)[]
        {
            (new Mulse.Hl7.Hl7MessageTypeAugmentModule().Descriptor, ["Message type namespace", "Output metadata key", "Overwrite existing"]),
            (new SetContextPropertyAugmentModule().Descriptor, ["Property name", "Property namespace", "Property value"]),
            (new Mulse.Hl7.Hl7XmlRenderModule().Descriptor, ["Trim trailing delimiters", "Segment terminator", "CRLF"]),
            (new Mulse.CompatibilityPack.SmtpEmailDeliverModule().Descriptor, ["Attachment name metadata key"])
        };
        foreach (var (descriptor, labels) in cases)
        {
            var html = await Render<ModuleSettingsFields>(new() { ["Descriptor"] = View(descriptor), ["Settings"] = new Dictionary<string, string>() });
            foreach (var label in labels)
            {
                Assert.Contains(label, html);
            }
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private string RecordPluginContext(IModuleCatalog catalog)
    {
        var lease = catalog.LeaseOrchestrationAugmentAsync("billing-field-augment", default).GetAwaiter().GetResult();
        try
        {
            var assembly = lease.Module.GetType().Assembly;
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly)!;
            Assert.True(context.IsCollectible);
            // A short weak reference can clear before finalization releases the loaded DLL.
            _unloadingContexts.Add(new WeakReference(context, trackResurrection: true));
            return assembly.Location;
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
        }
        Assert.All(_unloadingContexts, context => Assert.False(context.IsAlive, "The unloaded Billing plugin context is still rooted."));
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        Assert.False(Directory.Exists(_directory));
    }
}
