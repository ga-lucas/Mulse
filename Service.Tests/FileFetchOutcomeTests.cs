using System.Text.Json;
using Mulse.CompatibilityPack;

namespace Service.Tests;

public sealed class FileFetchOutcomeTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-file-outcomes-" + Guid.NewGuid());
    private string Input => Path.Combine(_directory, "input");
    private FlowExecutionContext Context => new("files", "stable-execution", DateTimeOffset.UtcNow);

    private ModuleStepDefinition Step(string success = "None", string failure = "None") => new()
    {
        Module = "file-system-fetch", Settings = new()
        {
            ["path"] = Input, ["recursive"] = "true", ["searchPattern"] = "*.txt",
            ["afterProcessing"] = success, ["archivePath"] = Path.Combine(_directory, "success"),
            ["onFailure"] = failure, ["failureArchivePath"] = Path.Combine(_directory, "failure")
        }
    };

    private async Task<string> WriteAsync(string name = "item.txt", string text = "original")
    {
        var path = Path.Combine(Input, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    private static FetchProcessingReceipt Receipt(IntegrationBatch batch, ModuleStepDefinition step)
        => new("stable-receipt", "fetch", step, new Dictionary<string, string>(batch.SourceReceipt!));

    [Theory]
    [InlineData("None")]
    [InlineData("Delete")]
    [InlineData("MoveToArchive")]
    [InlineData("Rename")]
    public async Task Fetch_DoesNotMutateSource_ThenSuccessCleanupIsIdempotent(string action)
    {
        var path = await WriteAsync(Path.Combine("nested", "item.txt"));
        var step = Step(action);
        var module = new FileSystemFetchModule();
        var batch = await module.FetchAsync(Context, IntegrationBatch.Empty, step, default);
        Assert.True(File.Exists(path));
        var receipt = Receipt(batch, step);
        await module.OnProcessingCompletedAsync(Context, receipt, new(true), default);
        await module.OnProcessingCompletedAsync(Context, receipt, new(true), default);
        Assert.Equal(action == "None", File.Exists(path));
        if (action == "MoveToArchive") Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_directory, "success", "nested", "item.txt")));
        if (action == "Rename") Assert.True(File.Exists(path + ".processed"));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Delete")]
    [InlineData("MoveToArchive")]
    [InlineData("Rename")]
    public async Task Failure_UsesOnlyTheFailureAction(string action)
    {
        var path = await WriteAsync();
        var step = Step("Delete", action);
        var module = new FileSystemFetchModule();
        var batch = await module.FetchAsync(Context, IntegrationBatch.Empty, step, default);
        Assert.True(File.Exists(path));
        var receipt = Receipt(batch, step);
        await module.OnProcessingCompletedAsync(Context, receipt, new(false, "destination unavailable"), default);
        await module.OnProcessingCompletedAsync(Context, receipt, new(false), default);
        Assert.Equal(action == "None", File.Exists(path));
        if (action == "MoveToArchive") Assert.True(File.Exists(Path.Combine(_directory, "failure", "item.txt")));
        if (action == "Rename") Assert.True(File.Exists(path + ".failed"));
    }

    [Theory]
    [InlineData("Delete")]
    [InlineData("MoveToArchive")]
    [InlineData("Rename")]
    public async Task ChangedSource_IsNeverRemovedEvenWithSameLengthAndTimestamp(string action)
    {
        var path = await WriteAsync(text: "original");
        var step = Step(action);
        var module = new FileSystemFetchModule();
        var batch = await module.FetchAsync(Context, IntegrationBatch.Empty, step, default);
        var timestamp = File.GetLastWriteTimeUtc(path);
        await File.WriteAllTextAsync(path, "replaced");
        File.SetLastWriteTimeUtc(path, timestamp);
        var error = await Assert.ThrowsAsync<IOException>(() => module.OnProcessingCompletedAsync(Context, Receipt(batch, step), new(true), default));
        Assert.Contains("changed", error.Message);
        Assert.Equal("replaced", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task Cleanup_UsesOriginalReceiptNotLaterSettingsOrNewFiles()
    {
        var original = await WriteAsync();
        var step = Step("Delete");
        var module = new FileSystemFetchModule();
        var batch = await module.FetchAsync(Context, IntegrationBatch.Empty, step, default);
        var added = await WriteAsync("later.txt");
        step.Settings["path"] = Path.Combine(_directory, "unrelated");
        step.Settings["afterProcessing"] = "None";
        await module.OnProcessingCompletedAsync(Context, Receipt(batch, step), new(true), default);
        Assert.False(File.Exists(original));
        Assert.True(File.Exists(added));
    }

    [Fact]
    public async Task ArchiveCollision_IsResolvedAtFetchAndNeverOverwritesExistingFiles()
    {
        await WriteAsync();
        var target = Path.Combine(_directory, "success", "item.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllTextAsync(target, "previous run");
        var step = Step("MoveToArchive");
        var module = new FileSystemFetchModule();
        var batch = await module.FetchAsync(Context, IntegrationBatch.Empty, step, default);
        var receipt = Receipt(batch, step);
        await module.OnProcessingCompletedAsync(Context, receipt, new(true), default);
        await module.OnProcessingCompletedAsync(Context, receipt, new(true), default);
        Assert.Equal("previous run", await File.ReadAllTextAsync(target));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(_directory, "success", "item-stable-execution.txt")));
    }

    [Fact]
    public async Task PartialRepositoryCleanup_RetriesSidecarWithoutMovingDocumentAgainOrTrustingMetadataPaths()
    {
        var path = await WriteAsync();
        var untouched = Path.Combine(_directory, "unrelated.txt");
        await File.WriteAllTextAsync(untouched, "do not touch");
        var sidecar = path + ".metadata.json";
        await File.WriteAllTextAsync(sidecar, JsonSerializer.Serialize(new Dictionary<string, string> { ["fullPath"] = untouched }));
        var step = Step("MoveToArchive");
        var module = new DocumentRepositoryFetchModule();
        var batch = await module.FetchAsync(Context, IntegrationBatch.Empty, step, default);
        Assert.True(File.Exists(path));
        Assert.True(File.Exists(sidecar));
        var receipt = Receipt(batch, step);
        var destination = Path.Combine(_directory, "success", "item.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Move(path, destination);
        await module.OnProcessingCompletedAsync(Context, receipt, new(true), default);
        await module.OnProcessingCompletedAsync(Context, receipt, new(true), default);
        Assert.True(File.Exists(destination + ".metadata.json"));
        Assert.False(File.Exists(sidecar));
        Assert.Equal("do not touch", await File.ReadAllTextAsync(untouched));
    }

    [Fact]
    public async Task DefaultFailureArchive_IsSiblingAndInvalidActionsFailBeforeCleanup()
    {
        var path = await WriteAsync();
        var step = Step("None", "MoveToArchive");
        step.Settings.Remove("failureArchivePath");
        var module = new FileSystemFetchModule();
        var batch = await module.FetchAsync(Context, IntegrationBatch.Empty, step, default);
        await module.OnProcessingCompletedAsync(Context, Receipt(batch, step), new(false), default);
        Assert.True(File.Exists(Path.Combine(Input + ".failed", "item.txt")));
        await WriteAsync();
        step.Settings["onFailure"] = "999";
        await Assert.ThrowsAsync<ArgumentException>(() => module.FetchAsync(Context, IntegrationBatch.Empty, step, default));
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldCheckpointSources_AreExcludedButNewFilesAreStillFetched(bool repository)
    {
        var original = await WriteAsync();
        IFetchOutcomeModule module = repository ? new DocumentRepositoryFetchModule() : new FileSystemFetchModule();
        var settings = Step("Delete").Settings;
        var step = new ModuleStepDefinition { Module = module.Descriptor.Id, Settings = settings };
        var first = await module.FetchAsync(Context, IntegrationBatch.Empty, step, default);
        var next = new FlowExecutionContext("files", "next", DateTimeOffset.UtcNow)
        {
            PendingSourceReceipts = [Receipt(first, step)]
        };
        var idle = await module.FetchAsync(next, IntegrationBatch.Empty, step, default);
        Assert.Empty(idle.Payloads);
        Assert.Null(idle.SourceReceipt);
        await WriteAsync("new.txt", "new message");
        var followup = await module.FetchAsync(next, IntegrationBatch.Empty, step, default);
        Assert.Equal("new.txt", Assert.Single(followup.Payloads).Name);
        Assert.True(File.Exists(original));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealFiles_SuspensionDoesNotReadOriginalAgain_AndCheckpointCompletionSurvivesDeliveryRetry(bool retryDelivery)
    {
        var original = await WriteAsync();
        var checkpoints = new InMemoryFlowOrchestrationStateStore();
        var retries = new InMemoryFlowRetryStateStore();
        var attempts = 0;
        var catalog = new FakeExecutableModuleCatalog().AddFetch(new FileSystemFetchModule())
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddAugment(new StatefulOrchestrationAugmentModule(checkpoints, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<StatefulOrchestrationAugmentModule>.Instance))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(new DelegateDeliverModule("deliver", (_, batch, _, _) =>
            {
                Assert.Equal(2, batch.Count);
                Assert.True(File.Exists(original));
                Assert.True(File.Exists(Path.Combine(Input, "new.txt")));
                if (++attempts == 1 && retryDelivery) throw new IOException("destination busy");
                return Task.CompletedTask;
            }));
        ModuleStepDefinition Orchestrate(string mode) => new()
        {
            Module = "stateful-orchestration-augment", Settings = new()
            {
                ["mode"] = mode, ["bookmark"] = "wait", ["correlationSource"] = "Literal", ["correlationValue"] = "key"
            }
        };
        var pipeline = new PipelineDefinition
        {
            Id = "files", Fetches = [new FetchNodeDefinition { Id = "f", Fetch = Step("Delete") }],
            Parses = [new ParseNodeDefinition { Id = "p", FetchNodeIds = ["f"], Parse = new ModuleStepDefinition { Module = "parse" } }],
            Augments = [Orchestrate("ResumeOrWait"), Orchestrate("Complete")],
            Deliveries = [new DeliveryRouteDefinition
            {
                Render = new ModuleStepDefinition { Module = "render" },
                Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition
                {
                    Module = "deliver", Retry = new RetryPolicyDefinition { MaxAttempts = 2, Delay = TimeSpan.Zero }
                } }]
            }]
        };
        var runtime = new FlowRuntime(new FakeFlowDefinitionService([pipeline]), catalog, checkpoints, retries,
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState())), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowRuntime>.Instance);
        Assert.Equal(FlowExecutionOutcome.Suspended, (await runtime.ExecuteAsync("files", default)).Outcome);
        var idle = await runtime.ExecuteAsync("files", default);
        Assert.Equal(FlowExecutionOutcome.Completed, idle.Outcome);
        Assert.Equal(0, idle.PayloadCount);
        Assert.Equal(0, attempts);
        Assert.Single(Assert.Single(checkpoints.Checkpoints).SourceReceipts);
        await WriteAsync("new.txt", "follow up");
        var result = await runtime.ExecuteAsync("files", default);
        if (retryDelivery)
        {
            Assert.Equal(FlowExecutionOutcome.Retrying, result.Outcome);
            Assert.Single(checkpoints.Checkpoints);
            Assert.Equal("files", Assert.Single(retries.States).CheckpointFlowId);
            result = (await runtime.ResumeRetryAsync(Assert.Single(retries.States), default))!;
        }
        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.PayloadCount);
        Assert.False(File.Exists(original));
        Assert.Empty(Directory.GetFiles(Input));
        Assert.Empty(checkpoints.Checkpoints);
        Assert.Empty(retries.States);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealFile_IsKeptThroughoutDeliveryAndRetries_ThenAcknowledgedOrQuarantined(bool permanent)
    {
        var path = await WriteAsync();
        var step = Step("Delete", "MoveToArchive");
        var fetch = new FileSystemFetchModule();
        var attempts = 0;
        var catalog = new FakeExecutableModuleCatalog().AddFetch(fetch)
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(new DelegateDeliverModule("deliver", (_, _, _, _) =>
            {
                Assert.True(File.Exists(path));
                if (++attempts == 1 || permanent) throw new IOException("destination unavailable");
                return Task.CompletedTask;
            }));
        var pipeline = new PipelineDefinition
        {
            Id = "files", Fetches = [new FetchNodeDefinition { Id = "f", Fetch = step }],
            Parses = [new ParseNodeDefinition { Id = "p", FetchNodeIds = ["f"], Parse = new ModuleStepDefinition { Module = "parse" } }],
            Deliveries = [new DeliveryRouteDefinition
            {
                Render = new ModuleStepDefinition { Module = "render" },
                Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition
                {
                    Module = "deliver", Retry = new RetryPolicyDefinition { MaxAttempts = 2, Delay = TimeSpan.Zero }
                } }]
            }]
        };
        var store = new InMemoryFlowRetryStateStore();
        var runtime = new FlowRuntime(new FakeFlowDefinitionService([pipeline]), catalog, new InMemoryFlowOrchestrationStateStore(), store,
            new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState())), TimeProvider.System,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FlowRuntime>.Instance);
        Assert.Equal(FlowExecutionOutcome.Retrying, (await runtime.ExecuteAsync("files", default)).Outcome);
        Assert.True(File.Exists(path));
        var result = await runtime.ResumeRetryAsync(Assert.Single(store.States), default);
        Assert.Equal(FlowExecutionOutcome.Completed, result!.Outcome);
        Assert.False(File.Exists(path));
        Assert.Equal(permanent, File.Exists(Path.Combine(_directory, "failure", "item.txt")));
        Assert.Equal(2, attempts);
        Assert.Empty(store.States);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
