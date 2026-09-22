using Microsoft.Extensions.Logging.Abstractions;

namespace Service.Tests;

public sealed class FlowRuntimeTests
{
    private static IntegrationBatch SinglePayloadBatch(string name = "payload-1", string content = "{}")
        => new([new IntegrationPayload(name, BinaryData.FromString(content), "application/json", new Dictionary<string, string>())]);

    private static (FlowRuntime Runtime, InMemoryFlowRetryStateStore RetryStore, InMemoryFlowOrchestrationStateStore OrchestrationStore) CreateRuntime(
        FakeExecutableModuleCatalog catalog, params PipelineDefinition[] pipelines)
    {
        var retryStore = new InMemoryFlowRetryStateStore();
        var orchestrationStore = new InMemoryFlowOrchestrationStateStore();
        var configResolver = new ConfigValueResolver(new FakeRuntimeConfigurationStore(new RuntimeMulseState()));
        var runtime = new FlowRuntime(
            new FakeFlowDefinitionService(pipelines),
            catalog,
            orchestrationStore,
            retryStore,
            configResolver,
            TimeProvider.System,
            NullLogger<FlowRuntime>.Instance);
        return (runtime, retryStore, orchestrationStore);
    }

    private static PipelineDefinition SingleSourceSingleRoutePipeline(
        string flowId = "flow-1",
        bool enabled = true,
        RetryPolicyDefinition? fetchRetry = null,
        string? atomicScope = null,
        ModuleStepDefinition? compensation = null)
        => new()
        {
            Id = flowId,
            Enabled = enabled,
            Sources =
            [
                new SourceDefinition
                {
                    Id = "primary",
                    Fetch = new ModuleStepDefinition { Module = "fake-fetch", Retry = fetchRetry },
                    Parse = new ModuleStepDefinition { Module = "fake-parse" }
                }
            ],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Deliver = new ModuleStepDefinition { Module = "fake-deliver" },
                    AtomicScope = atomicScope,
                    Compensation = compensation
                }
            ]
        };

    [Fact]
    public async Task ExecuteAsync_HappyPath_ReturnsCompletedAndDeliversPayload()
    {
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch()))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("fake-deliver", delivered));

        var (runtime, retryStore, _) = CreateRuntime(catalog, SingleSourceSingleRoutePipeline());

        var result = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.PayloadCount);
        Assert.Equal(["fake-deliver"], result.DeliverModules);
        Assert.Single(delivered);
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ExecuteAsync_DisabledFlow_ThrowsInvalidOperationException()
    {
        var catalog = new FakeExecutableModuleCatalog();
        var (runtime, _, _) = CreateRuntime(catalog, SingleSourceSingleRoutePipeline(enabled: false));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ExecuteAsync("flow-1", CancellationToken.None));
        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_FlowWithNoSources_ThrowsInvalidOperationException()
    {
        var catalog = new FakeExecutableModuleCatalog();
        var pipeline = new PipelineDefinition { Id = "flow-1", Enabled = true };
        var (runtime, _, _) = CreateRuntime(catalog, pipeline);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ExecuteAsync("flow-1", CancellationToken.None));
        Assert.Contains("does not define any sources", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_DependentSource_ReceivesUpstreamSourceParsedPayloadAsFetchInput()
    {
        IntegrationBatch? observedInput = null;
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("primary-fetch", SinglePayloadBatch("primary-payload")))
            .AddParse(DelegateModuleFactory.PassthroughParse("primary-parse"))
            .AddFetch(new DelegateFetchModule("lookup-fetch", (_, input, _, _) =>
            {
                observedInput = input;
                return Task.FromResult(SinglePayloadBatch("lookup-payload"));
            }))
            .AddParse(DelegateModuleFactory.PassthroughParse("lookup-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("fake-deliver"));

        var pipeline = new PipelineDefinition
        {
            Id = "flow-1",
            Enabled = true,
            Sources =
            [
                new SourceDefinition
                {
                    Id = "primary",
                    Fetch = new ModuleStepDefinition { Module = "primary-fetch" },
                    Parse = new ModuleStepDefinition { Module = "primary-parse" }
                },
                new SourceDefinition
                {
                    Id = "lookup",
                    Fetch = new ModuleStepDefinition { Module = "lookup-fetch" },
                    Parse = new ModuleStepDefinition { Module = "lookup-parse" },
                    InputSourceIds = ["primary"]
                }
            ],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Deliver = new ModuleStepDefinition { Module = "fake-deliver" }
                }
            ]
        };

        var (runtime, _, _) = CreateRuntime(catalog, pipeline);
        var result = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        Assert.NotNull(observedInput);
        Assert.Single(observedInput!.Payloads);
        Assert.Equal("primary-payload", observedInput.Payloads[0].Name);
        // Both source's payloads (2 total) should have reached the merged augment/delivery input.
        Assert.Equal(2, result.PayloadCount);
    }

    [Fact]
    public async Task ExecuteAsync_StageFailsWithRetryAllowed_ReturnsRetryingAndPersistsRetryState()
    {
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch(), failOnCall: call => call == 1))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("fake-deliver"));

        var retryPolicy = new RetryPolicyDefinition { MaxAttempts = 2, Delay = TimeSpan.FromMilliseconds(1) };
        var (runtime, retryStore, _) = CreateRuntime(catalog, SingleSourceSingleRoutePipeline(fetchRetry: retryPolicy));

        var result = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Retrying, result.Outcome);
        Assert.NotNull(result.NextAttemptAt);
        var pending = Assert.Single(retryStore.States);
        Assert.Equal("flow-1", pending.FlowId);
        Assert.Equal("source:primary:fetch", pending.StageId);
        Assert.Equal(1, pending.AttemptsMade);
    }

    [Fact]
    public async Task ResumeRetryAsync_AfterScheduledRetry_CompletesAndClearsRetryState()
    {
        var callCount = 0;
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("fake-fetch", (_, input, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                {
                    throw new InvalidOperationException("transient failure");
                }

                return Task.FromResult(SinglePayloadBatch());
            }))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("fake-deliver", delivered));

        var retryPolicy = new RetryPolicyDefinition { MaxAttempts = 2, Delay = TimeSpan.FromMilliseconds(1) };
        var (runtime, retryStore, _) = CreateRuntime(catalog, SingleSourceSingleRoutePipeline(fetchRetry: retryPolicy));

        var scheduled = await runtime.ExecuteAsync("flow-1", CancellationToken.None);
        Assert.Equal(FlowExecutionOutcome.Retrying, scheduled.Outcome);
        var pendingState = Assert.Single(retryStore.States);

        var resumed = await runtime.ResumeRetryAsync(pendingState, CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.Equal(FlowExecutionOutcome.Completed, resumed!.Outcome);
        Assert.Single(delivered);
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ExecuteAsync_StageFailsWithNoRetriesAllowed_ThrowsAndLeavesNoRetryState()
    {
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch(), failOnCall: call => true))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("fake-deliver"));

        var (runtime, retryStore, _) = CreateRuntime(catalog, SingleSourceSingleRoutePipeline());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync("flow-1", CancellationToken.None));
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ExecuteAsync_AtomicScopeDeliveryFailsPermanently_CompensatesEarlierCommittedDelivery()
    {
        var compensated = new List<IntegrationBatch>();
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch()))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver-ok", delivered))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver-fails", failOnCall: _ => true))
            .AddDeliver(DelegateModuleFactory.Deliver("compensate", compensated));

        var pipeline = new PipelineDefinition
        {
            Id = "flow-1",
            Enabled = true,
            Sources =
            [
                new SourceDefinition
                {
                    Id = "primary",
                    Fetch = new ModuleStepDefinition { Module = "fake-fetch" },
                    Parse = new ModuleStepDefinition { Module = "fake-parse" }
                }
            ],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Deliver = new ModuleStepDefinition { Module = "deliver-ok" },
                    AtomicScope = "scope-1",
                    Compensation = new ModuleStepDefinition { Module = "compensate" }
                },
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Deliver = new ModuleStepDefinition { Module = "deliver-fails" },
                    AtomicScope = "scope-1"
                }
            ]
        };

        var (runtime, retryStore, _) = CreateRuntime(catalog, pipeline);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync("flow-1", CancellationToken.None));

        Assert.Single(delivered);
        Assert.Single(compensated);
        Assert.Empty(retryStore.States);
    }
}
