using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Service.Endpoints;
using Service.Middleware;
using Service.Models;
using Service.Triggers;

namespace Service.Tests;

public sealed class FlowMigrationReadinessTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly FakeModuleCatalog Catalog = FakeModuleCatalog.WithModules(
        ("fetch", ModuleKind.Fetch), ("parse", ModuleKind.Parse), ("render", ModuleKind.Render), ("deliver", ModuleKind.Deliver));

    private static FlowMigrationReport Report(MigrationTranslationStatus status = MigrationTranslationStatus.Unsupported)
        => new("BizTalk", @"C:\migration\Orders.btproj", "Orders",
            [new("Map", @"C:\migration\Orders.btm", status, [])], [], []);

    private static PipelineDefinition Pipeline(bool enabled, FlowMigrationReport? report = null, PipelineTriggerMode mode = PipelineTriggerMode.OnDemand)
        => new()
        {
            Id = "orders", Enabled = enabled, Migration = report,
            Trigger = new() { Mode = mode, Interval = TimeSpan.FromSeconds(1), RunOnStartup = true },
            Fetches = [new() { Id = "f", Fetch = new() { Module = "fetch" } }],
            Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
            Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }]
        };

    private static FlowRuntime Runtime(IFlowDefinitionService flows, IModuleCatalog? catalog = null, IFlowRetryStateStore? retries = null)
        => new(flows, catalog ?? Catalog, new InMemoryFlowOrchestrationStateStore(), retries ?? new InMemoryFlowRetryStateStore(),
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState())), TimeProvider.System, NullLogger<FlowRuntime>.Instance);

    [Theory]
    [InlineData(MigrationTranslationStatus.FullyTranslated)]
    [InlineData(MigrationTranslationStatus.ReviewRequired)]
    [InlineData(MigrationTranslationStatus.StubGenerated)]
    [InlineData(MigrationTranslationStatus.Unsupported)]
    [InlineData(MigrationTranslationStatus.ConfigurationRequired)]
    public async Task EnabledCreation_RejectsEverySubmittedStatusWithoutChangingStore(MigrationTranslationStatus status)
    {
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState());
        var service = new FlowDefinitionService(store, Catalog);
        var exception = await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => service.CreateAsync(Pipeline(true, Report(status)), default));
        Assert.Contains("verified translation support", exception.Message);
        Assert.Empty(store.GetState().Pipelines);
        var draft = await service.CreateAsync(Pipeline(false, Report(status)), default);
        Assert.False(draft.Enabled);
        Assert.NotNull(draft.Migration);
    }

    [Fact]
    public async Task DesignedCreation_UsesSameGateAndStillAllowsDisabledDraft()
    {
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState());
        var definitions = new FlowDefinitionService(store, Catalog);
        var designer = new FlowDesignService(Catalog, definitions);
        var request = JsonSerializer.Deserialize<CreateDesignedFlowRequest>(JsonSerializer.Serialize(FlowMappings.MapFlow(Pipeline(true, Report())), JsonOptions), JsonOptions)!;
        await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => designer.CreateFlowAsync(request, default));
        Assert.Empty(store.GetState().Pipelines);
        Assert.False((await designer.CreateFlowAsync(request with { Enabled = false }, default)).Enabled);
    }

    [Fact]
    public async Task EmptyAndFutureReports_CannotApproveExecution()
    {
        var report = new FlowMigrationReport("Anything", "", null, [], [], []) { Version = 999 };
        await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => Runtime(new FakeFlowDefinitionService(Pipeline(true, report))).ExecuteAsync("orders", default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Update_RetainsSavedReportAgainstOmissionOrReplacementAndBlocksEnable(bool omit)
    {
        var original = Pipeline(false, Report());
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState { Pipelines = [original] });
        var service = new FlowDefinitionService(store, Catalog);
        var submitted = omit ? null : new FlowMigrationReport("Approved", "unrelated", null, [], [], []);
        if (!omit)
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateAsync("orders", Pipeline(false, submitted), default));
            Assert.Contains("provenance cannot be replaced", exception.Message);
        }
        var updated = await service.UpdateAsync("orders", Pipeline(false, omit ? null : original.Migration), default);
        Assert.Same(original.Migration, updated.Migration);
        await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => service.UpdateAsync("orders", Pipeline(true, omit ? null : original.Migration), default));
        Assert.False(service.GetById("orders").Enabled);
        Assert.Same(original.Migration, service.GetById("orders").Migration);
    }

    [Fact]
    public async Task LegacyEnabledImport_CanBeDisabledCategorizedAndDuplicatedButNotRun()
    {
        var original = Pipeline(true, Report());
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState { Pipelines = [original] });
        var service = new FlowDefinitionService(store, Catalog);
        await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => Runtime(service).ExecuteAsync("orders", default));
        var disabled = await service.UpdateAsync("orders", FlowMappings.WithEnabled(original, false), default);
        var categorized = await service.UpdateAsync("orders", FlowMappings.CopyPipeline(disabled, disabled.Id, false, "Migration"), default);
        Assert.Equal("Migration", categorized.Category);
        var copy = await service.DuplicateAsync("orders", "copy", default);
        Assert.False(copy.Enabled);
        Assert.NotNull(copy.Migration);
        await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => Runtime(service).ExecuteAsync("copy", default));
    }

    [Theory]
    [InlineData(PipelineTriggerMode.OnDemand)]
    [InlineData(PipelineTriggerMode.Interval)]
    [InlineData(PipelineTriggerMode.Push)]
    public async Task Runtime_BlocksBeforeAnyModuleLeaseForEveryTriggerMode(PipelineTriggerMode mode)
    {
        // Catalog throws on any lease, so a migration conflict proves processing was never reached.
        var runtime = Runtime(new FakeFlowDefinitionService(Pipeline(true, Report(), mode)));
        await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => runtime.ExecuteAsync("orders", default));
    }

    [Theory]
    [InlineData("fetch:f")]
    [InlineData("parse:p")]
    [InlineData("augment:0")]
    [InlineData("delivery:0:render")]
    [InlineData("delivery:0:deliver:0")]
    public async Task ProcessingRetry_BlocksBeforeLeasesAndRetainsPendingState(string stage)
    {
        var retries = new InMemoryFlowRetryStateStore();
        var state = new FlowRetryState { FlowId = "orders", ExecutionId = "execution", StageId = stage };
        await retries.UpsertAsync(state, default);
        var runtime = Runtime(new FakeFlowDefinitionService(Pipeline(true, Report())), retries: retries);
        await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => runtime.ResumeRetryAsync(state, default));
        Assert.Same(state, Assert.Single(retries.States));
    }

    [Fact]
    public async Task PushRegistration_DoesNotLeaseImportedSourceEvenForLegacyEnabledFlow()
    {
        using var service = new PushTriggeredFlowService(Runtime(new FakeFlowDefinitionService(Pipeline(true, Report(), PipelineTriggerMode.Push))),
            Catalog, NullLogger<PushTriggeredFlowService>.Instance);
        var reconcile = typeof(PushTriggeredFlowService).GetMethod("ReconcileRegistrationsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)reconcile.Invoke(service, [CancellationToken.None])!;
        var registrations = (System.Collections.IDictionary)typeof(PushTriggeredFlowService)
            .GetField("_activeRegistrations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        Assert.Empty(registrations);
    }

    [Fact]
    public async Task ManualFlow_RemainsCreatableEnabledAndExecutable()
    {
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState());
        var service = new FlowDefinitionService(store, Catalog);
        var flow = await service.CreateAsync(Pipeline(true), default);
        Assert.True(flow.Enabled);
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fetch", new IntegrationBatch([])))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
        Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(service, catalog).ExecuteAsync("orders", default)).Outcome);
    }

    [Fact]
    public async Task ReloadAndRestore_RetainGateEvenWithFullyTranslatedSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-readiness-" + Guid.NewGuid().ToString("N"));
        RuntimeConfigurationStore Store() => new(Microsoft.Extensions.Options.Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json") }),
            new FakeHostEnvironment(), Mulse.Modules.Security.NoOpRuntimeStatePayloadProtector.Instance);
        try
        {
            await Store().UpsertFlowAsync(Pipeline(true, Report(MigrationTranslationStatus.FullyTranslated)), default);
            var reloaded = Store();
            var service = new FlowDefinitionService(reloaded, Catalog);
            await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => Runtime(service).ExecuteAsync("orders", default));
            await reloaded.RestoreWorkspaceAsync(reloaded.GetState(), true, default);
            var restored = Store();
            Assert.True(restored.GetState().Pipelines.Single().Enabled);
            var restoredService = new FlowDefinitionService(restored, Catalog);
            await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => Runtime(restoredService).ExecuteAsync("orders", default));
            await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => restoredService.UpdateAsync("orders", FlowMappings.WithEnabled(restoredService.GetById("orders"), true), default));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Scheduler_ReportsMigrationConflictWithoutLeasingModules()
    {
        var logger = new ExceptionLogger<ScheduledFlowService>();
        using var scheduler = new ScheduledFlowService(Runtime(new FakeFlowDefinitionService(Pipeline(true, Report(), PipelineTriggerMode.Interval))), TimeProvider.System, logger);
        await scheduler.StartAsync(default);
        try
        {
            Assert.IsType<FlowMigrationBlockedException>(await logger.Exception.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { await scheduler.StopAsync(default); }
    }

    [Fact]
    public async Task TerminalSourceNotifications_CanFinishWithoutNewProcessingOrDeliveries()
    {
        var source = new OutcomeSource();
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog().AddFetch(source)
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
        var retries = new InMemoryFlowRetryStateStore();
        await Assert.ThrowsAsync<AggregateException>(() => Runtime(new FakeFlowDefinitionService(Pipeline(true)), catalog, retries).ExecuteAsync("orders", default));
        Assert.True(Assert.Single(retries.States).SourceCompletion!.NotificationsExhausted);
        var blocked = Runtime(new FakeFlowDefinitionService(Pipeline(false, Report())), catalog, retries);
        Assert.Equal(FlowExecutionOutcome.Completed, (await blocked.RetrySourceOutcomesAsync("orders", default)).Outcome);
        Assert.Equal(1, source.Fetches);
        Assert.Equal(2, source.Callbacks);
        Assert.Single(delivered);
        Assert.Empty(retries.States);
        await Assert.ThrowsAsync<FlowMigrationBlockedException>(() => blocked.ExecuteAsync("orders", default));
    }

    [Fact]
    public async Task RealEndpoints_RejectEnableRunAndTamperedUpdatesButAllowDisabledSaves()
    {
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState());
        var definitions = new FlowDefinitionService(store, Catalog);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(store);
        builder.Services.AddSingleton<IModuleCatalog>(Catalog);
        builder.Services.AddSingleton<IFlowDefinitionService>(definitions);
        builder.Services.AddSingleton<IFlowRuntime>(Runtime(definitions));
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapFlowEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var request = JsonSerializer.Deserialize<CreateFlowRequest>(JsonSerializer.Serialize(FlowMappings.MapFlow(Pipeline(true, Report())), JsonOptions), JsonOptions)!;
            using var deniedCreate = await http.PostAsJsonAsync("/api/flows", request, JsonOptions);
            Assert.Equal(HttpStatusCode.Conflict, deniedCreate.StatusCode);
            Assert.Empty(store.GetState().Pipelines);
            using var created = await http.PostAsJsonAsync("/api/flows", request with { Enabled = false }, JsonOptions);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var enable = await http.PatchAsJsonAsync("/api/flows/orders/enabled", new { Enabled = true });
            Assert.Equal(HttpStatusCode.Conflict, enable.StatusCode);
            using var run = await http.PostAsync("/api/flows/orders/run", null);
            Assert.Equal(HttpStatusCode.Conflict, run.StatusCode);
            Assert.Contains("verified translation support", await run.Content.ReadAsStringAsync());
            var update = JsonSerializer.Deserialize<UpdateFlowRequest>(JsonSerializer.Serialize(request, JsonOptions), JsonOptions)! with { Migration = null };
            using var deniedUpdate = await http.PutAsJsonAsync("/api/flows/orders", update, JsonOptions);
            Assert.Equal(HttpStatusCode.Conflict, deniedUpdate.StatusCode);
            using var replaced = await http.PutAsJsonAsync("/api/flows/orders", update with { Enabled = false, Migration = Report(MigrationTranslationStatus.FullyTranslated) }, JsonOptions);
            Assert.Equal(HttpStatusCode.Conflict, replaced.StatusCode);
            Assert.Contains("provenance cannot be replaced", await replaced.Content.ReadAsStringAsync());
            using var saved = await http.PutAsJsonAsync("/api/flows/orders", update with { Enabled = false }, JsonOptions);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(MigrationTranslationStatus.Unsupported, definitions.GetById("orders").Migration!.Artifacts.Single().Status);
            Assert.False(definitions.GetById("orders").Enabled);
            using var duplicate = await http.PostAsJsonAsync("/api/flows/orders/duplicate", new { Id = "copy" });
            Assert.Equal(HttpStatusCode.Created, duplicate.StatusCode);
            using var enableCopy = await http.PatchAsJsonAsync("/api/flows/copy/enabled", new { Enabled = true });
            Assert.Equal(HttpStatusCode.Conflict, enableCopy.StatusCode);
            using var disable = await http.PatchAsJsonAsync("/api/flows/orders/enabled", new { Enabled = false });
            Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class ExceptionLogger<T> : ILogger<T>
    {
        public TaskCompletionSource<Exception> Exception { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (exception is not null) Exception.TrySetResult(exception);
        }
    }

    private sealed class OutcomeSource : IFetchOutcomeModule
    {
        public ModuleDescriptor Descriptor { get; } = new("fetch", "fetch", ModuleKind.Fetch, "test");
        public int Fetches { get; private set; }
        public int Callbacks { get; private set; }
        public Task<IntegrationBatch> FetchAsync(FlowExecutionContext context, IntegrationBatch input, ModuleStepDefinition step, CancellationToken cancellationToken)
        {
            Fetches++;
            return Task.FromResult(new IntegrationBatch([new("item", BinaryData.FromString("{}"), "application/json", new Dictionary<string, string>())])
                { SourceReceipt = new Dictionary<string, string> { ["identity"] = "item" } });
        }
        public Task OnProcessingCompletedAsync(FlowExecutionContext context, FetchProcessingReceipt receipt, FetchProcessingOutcome outcome, CancellationToken cancellationToken)
        {
            if (++Callbacks == 1) throw new InvalidOperationException("synthetic callback failure");
            return Task.CompletedTask;
        }
    }
}
