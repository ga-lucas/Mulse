using System.Text.Json;
using Microsoft.Extensions.Options;
using Mulse.Modules;
using Mulse.Modules.Security;
using Service;

namespace Service.Tests;

public sealed class FlowManagementTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-flow-management-" + Guid.NewGuid());
    private static readonly FakeModuleCatalog Catalog = FakeModuleCatalog.WithModules(
        ("fetch", ModuleKind.Fetch), ("parse", ModuleKind.Parse),
        ("augment", ModuleKind.OrchestrationAugment), ("render", ModuleKind.Render), ("deliver", ModuleKind.Deliver));

    private RuntimeConfigurationStore Store() => new(
        Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }),
        new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);

    private static PipelineDefinition Pipeline() => new()
    {
        Id = "orders",
        Name = "Customer orders",
        Category = "Sales",
        Enabled = true,
        Trigger = new PipelineTriggerOptions { Mode = PipelineTriggerMode.Interval, Interval = TimeSpan.FromMinutes(5), RunOnStartup = true },
        Retry = new RetryPolicyDefinition { MaxAttempts = 3, Delay = TimeSpan.FromSeconds(2) },
        Fetches = [new FetchNodeDefinition { Id = "in", Fetch = new ModuleStepDefinition { Module = "fetch", Name = "Input", Settings = new() { ["path"] = "{{config:orders-path}}" } } }],
        Parses = [new ParseNodeDefinition { Id = "parsed", FetchNodeIds = ["in"], Parse = new ModuleStepDefinition { Module = "parse" } }],
        Augments = [new ModuleStepDefinition { Module = "augment", Settings = new() { ["mode"] = "merge" } }],
        Deliveries = [new DeliveryRouteDefinition
        {
            AtomicScope = "commit",
            Render = new ModuleStepDefinition { Module = "render" },
            Delivers = [new DeliveryActionDefinition
            {
                Deliver = new ModuleStepDefinition { Module = "deliver" },
                Compensation = new ModuleStepDefinition { Module = "deliver", Settings = new() { ["undo"] = "true" } }
            }]
        }]
    };

    [Fact]
    public async Task Duplicate_PersistsDisabledIndependentDefinitionWithoutExecutionState()
    {
        var store = Store();
        var service = new FlowDefinitionService(store, Catalog);
        var original = await service.CreateAsync(Pipeline(), CancellationToken.None);
        await store.UpsertRetryStateAsync(new FlowRetryState { FlowId = "orders", ExecutionId = "e", StageId = "fetch:0" }, CancellationToken.None);
        await store.UpsertOrchestrationCheckpointAsync(new FlowOrchestrationCheckpoint { FlowId = "orders", CorrelationKey = "c" }, CancellationToken.None);

        var copy = await service.DuplicateAsync("ORDERS", " orders-copy ", CancellationToken.None);

        Assert.Equal("orders-copy", copy.Id);
        Assert.False(copy.Enabled);
        Assert.True(service.GetById("orders").Enabled);
        Assert.Equal(original.Category, copy.Category);
        Assert.Equal(original.Name, copy.Name);
        Assert.Equal(JsonSerializer.Serialize(original.Trigger), JsonSerializer.Serialize(copy.Trigger));
        Assert.Equal(JsonSerializer.Serialize(original.Fetches), JsonSerializer.Serialize(copy.Fetches));
        Assert.Equal(JsonSerializer.Serialize(original.Parses), JsonSerializer.Serialize(copy.Parses));
        Assert.Equal(JsonSerializer.Serialize(original.Augments), JsonSerializer.Serialize(copy.Augments));
        Assert.Equal(JsonSerializer.Serialize(original.Deliveries), JsonSerializer.Serialize(copy.Deliveries));
        Assert.Equal(JsonSerializer.Serialize(original.Retry), JsonSerializer.Serialize(copy.Retry));
        copy.Fetches[0].Fetch.Settings["path"] = "changed";
        Assert.Equal("{{config:orders-path}}", service.GetById("orders").Fetches[0].Fetch.Settings["path"]);
        var reloaded = Store().GetState();
        Assert.Equal(2, reloaded.Pipelines.Count);
        Assert.False(reloaded.Pipelines.Single(flow => flow.Id == "orders-copy").Enabled);
        Assert.Equal("Sales", reloaded.Pipelines.Single(flow => flow.Id == "orders-copy").Category);
        Assert.Equal("Customer orders", reloaded.Pipelines.Single(flow => flow.Id == "orders-copy").Name);
        Assert.All(reloaded.PendingRetries, retry => Assert.Equal("orders", retry.FlowId));
        Assert.All(reloaded.OrchestrationCheckpoints, checkpoint => Assert.Equal("orders", checkpoint.FlowId));
    }

    [Fact]
    public async Task Duplicate_RejectsExistingIdMissingSourceAndEmptyIdWithoutChangingFlows()
    {
        var service = new FlowDefinitionService(Store(), Catalog);
        await service.CreateAsync(Pipeline(), CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DuplicateAsync("orders", "ORDERS", CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DuplicateAsync("missing", "new", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.DuplicateAsync("orders", " ", CancellationToken.None));
        Assert.Single(service.GetAll());
    }

    [Fact]
    public async Task Category_IsNormalizedPreservedOnEnableAndClearedPersistently()
    {
        var service = new FlowDefinitionService(Store(), Catalog);
        var original = await service.CreateAsync(Pipeline(), CancellationToken.None);
        var categorized = await service.UpdateAsync(original.Id, FlowMappings.CopyPipeline(original, original.Id, original.Enabled, "  Finance  "), CancellationToken.None);
        Assert.Equal("Finance", FlowMappings.MapFlow(categorized).Category);
        var disabled = await service.UpdateAsync(original.Id, FlowMappings.WithEnabled(categorized, false), CancellationToken.None);
        Assert.Equal("Finance", Store().GetState().Pipelines.Single().Category);
        Assert.Equal("Customer orders", Store().GetState().Pipelines.Single().Name);
        Assert.False(disabled.Enabled);
        await service.UpdateAsync(original.Id, FlowMappings.CopyPipeline(disabled, disabled.Id, disabled.Enabled, "  "), CancellationToken.None);
        Assert.Null(Store().GetState().Pipelines.Single().Category);
    }

    [Fact]
    public async Task Delete_RemovesOnlySelectedFlowAndRemainsDeletedAfterReload()
    {
        var service = new FlowDefinitionService(Store(), Catalog);
        await service.CreateAsync(Pipeline(), CancellationToken.None);
        await service.DuplicateAsync("orders", "copy", CancellationToken.None);
        await service.DeleteAsync("ORDERS", CancellationToken.None);
        Assert.Equal("copy", Store().GetState().Pipelines.Single().Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteAsync("missing", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
