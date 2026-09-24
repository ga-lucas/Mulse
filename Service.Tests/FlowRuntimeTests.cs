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
            Fetches =
            [
                new FetchNodeDefinition
                {
                    Id = "primary-fetch",
                    Fetch = new ModuleStepDefinition { Module = "fake-fetch", Retry = fetchRetry }
                }
            ],
            Parses =
            [
                new ParseNodeDefinition
                {
                    Id = "primary",
                    Parse = new ModuleStepDefinition { Module = "fake-parse" },
                    FetchNodeIds = ["primary-fetch"]
                }
            ],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "fake-deliver" }, Compensation = compensation }],
                    AtomicScope = atomicScope
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
    public async Task ExecuteAsync_FlowWithEmptySourceGraph_ThrowsInvalidOperationException()
    {
        var catalog = new FakeExecutableModuleCatalog();
        var pipeline = new PipelineDefinition { Id = "flow-1", Enabled = true };
        var (runtime, _, _) = CreateRuntime(catalog, pipeline);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.ExecuteAsync("flow-1", CancellationToken.None));
        Assert.Contains("does not define any fetch and parse nodes", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_DependentFetch_ReceivesUpstreamParseNodeOutputAsFetchInput()
    {
        IntegrationBatch? observedInput = null;
        var delivered = new List<IntegrationBatch>();
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
            .AddDeliver(DelegateModuleFactory.Deliver("fake-deliver", delivered));

        var pipeline = new PipelineDefinition
        {
            Id = "flow-1",
            Enabled = true,
            // Declared out of dependency order on purpose: the combined topological sort must still run
            // primary-fetch -> primary -> lookup-fetch -> lookup.
            Fetches =
            [
                new FetchNodeDefinition
                {
                    Id = "lookup-fetch",
                    Fetch = new ModuleStepDefinition { Module = "lookup-fetch" },
                    InputParseIds = ["primary"]
                },
                new FetchNodeDefinition
                {
                    Id = "primary-fetch",
                    Fetch = new ModuleStepDefinition { Module = "primary-fetch" }
                }
            ],
            Parses =
            [
                new ParseNodeDefinition
                {
                    Id = "lookup",
                    Parse = new ModuleStepDefinition { Module = "lookup-parse" },
                    FetchNodeIds = ["lookup-fetch"]
                },
                new ParseNodeDefinition
                {
                    Id = "primary",
                    Parse = new ModuleStepDefinition { Module = "primary-parse" },
                    FetchNodeIds = ["primary-fetch"]
                }
            ],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "fake-deliver" } }]
                }
            ]
        };

        var (runtime, _, _) = CreateRuntime(catalog, pipeline);
        var result = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        Assert.NotNull(observedInput);
        Assert.Single(observedInput!.Payloads);
        Assert.Equal("primary-payload", observedInput.Payloads[0].Name);
        Assert.Equal("primary", observedInput.Payloads[0].Metadata["sourceId"]);
        // Both parse nodes' payloads (2 total) should have reached the merged augment/delivery input, each tagged
        // with the id of the parse node that produced it.
        Assert.Equal(2, result.PayloadCount);
        var deliveredBatch = Assert.Single(delivered);
        Assert.Equal(
            ["primary-payload:primary", "lookup-payload:lookup"],
            deliveredBatch.Payloads.Select(static payload => $"{payload.Name}:{payload.Metadata["sourceId"]}"));
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
        Assert.Equal("fetch:primary-fetch", pending.StageId);
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
            Fetches = [new FetchNodeDefinition { Id = "primary-fetch", Fetch = new ModuleStepDefinition { Module = "fake-fetch" } }],
            Parses = [new ParseNodeDefinition { Id = "primary", Parse = new ModuleStepDefinition { Module = "fake-parse" }, FetchNodeIds = ["primary-fetch"] }],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Delivers =
                    [
                        new DeliveryActionDefinition
                        {
                            Deliver = new ModuleStepDefinition { Module = "deliver-ok" },
                            Compensation = new ModuleStepDefinition { Module = "compensate" }
                        }
                    ],
                    AtomicScope = "scope-1"
                },
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "deliver-fails" } }],
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

    private static PipelineDefinition FanOutPipeline(string? atomicScope, params DeliveryActionDefinition[] delivers)
        => new()
        {
            Id = "flow-1",
            Enabled = true,
            Fetches = [new FetchNodeDefinition { Id = "primary-fetch", Fetch = new ModuleStepDefinition { Module = "fake-fetch" } }],
            Parses = [new ParseNodeDefinition { Id = "primary", Parse = new ModuleStepDefinition { Module = "fake-parse" }, FetchNodeIds = ["primary-fetch"] }],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Delivers = delivers,
                    AtomicScope = atomicScope
                }
            ]
        };

    private static DeliveryActionDefinition DeliverAction(string module, string? compensation = null, RetryPolicyDefinition? retry = null)
        => new()
        {
            Deliver = new ModuleStepDefinition { Module = module, Retry = retry },
            Compensation = compensation is null ? null : new ModuleStepDefinition { Module = compensation }
        };

    [Fact]
    public async Task ExecuteAsync_RouteWithTwoDelivers_RendersOnceAndDeliversSameBatchToBoth()
    {
        var renderCalls = 0;
        var emailed = new List<IntegrationBatch>();
        var archived = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch("order.json", "{\"id\":1}")))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(new DelegateRenderModule("fake-render", (_, batch, _, _) =>
            {
                renderCalls++;
                return Task.FromResult(batch);
            }))
            .AddDeliver(DelegateModuleFactory.Deliver("email-deliver", emailed))
            .AddDeliver(DelegateModuleFactory.Deliver("archive-deliver", archived));

        var (runtime, retryStore, _) = CreateRuntime(catalog, FanOutPipeline(null, DeliverAction("email-deliver"), DeliverAction("archive-deliver")));

        var result = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        Assert.Null(result.Detail);
        Assert.Equal(["email-deliver", "archive-deliver"], result.DeliverModules);
        Assert.Equal(1, renderCalls);
        var emailedBatch = Assert.Single(emailed);
        var archivedBatch = Assert.Single(archived);
        Assert.Equal("order.json", Assert.Single(emailedBatch.Payloads).Name);
        Assert.Equal(
            emailedBatch.Payloads.Select(static payload => (payload.Name, payload.Content.ToString())),
            archivedBatch.Payloads.Select(static payload => (payload.Name, payload.Content.ToString())));
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ExecuteAsync_IndependentDeliverFailsPermanently_StillRunsRemainingDeliversAndCompletes()
    {
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch()))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("sftp-deliver", failOnCall: _ => true))
            .AddDeliver(DelegateModuleFactory.Deliver("file-deliver", delivered));

        var (runtime, retryStore, _) = CreateRuntime(catalog, FanOutPipeline(null, DeliverAction("sftp-deliver"), DeliverAction("file-deliver")));

        var result = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        Assert.Single(delivered);
        Assert.NotNull(result.Detail);
        Assert.Contains("1 deliver action(s) failed", result.Detail);
        Assert.Contains("sftp-deliver", result.Detail);
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ExecuteAsync_AtomicScopeSecondDeliverInSameRouteFails_CompensatesFirstDeliverOfThatRoute()
    {
        var compensated = new List<IntegrationBatch>();
        var delivered = new List<IntegrationBatch>();
        var thirdDelivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch()))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver-ok", delivered))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver-fails", failOnCall: _ => true))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver-after", thirdDelivered))
            .AddDeliver(DelegateModuleFactory.Deliver("compensate", compensated));

        var pipeline = FanOutPipeline(
            "scope-1",
            DeliverAction("deliver-ok", compensation: "compensate"),
            DeliverAction("deliver-fails"),
            DeliverAction("deliver-after"));
        var (runtime, retryStore, _) = CreateRuntime(catalog, pipeline);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync("flow-1", CancellationToken.None));

        Assert.Single(delivered);
        Assert.Single(compensated);
        // The scope is considered failed once any of its deliver actions fails, so the route's remaining actions are skipped.
        Assert.Empty(thirdDelivered);
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ExecuteAsync_AtomicScopeRouteFails_UnrelatedRoutesStillRun()
    {
        var unrelatedDelivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch()))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver-fails", failOnCall: _ => true))
            .AddDeliver(DelegateModuleFactory.Deliver("unrelated-deliver", unrelatedDelivered));

        var pipeline = new PipelineDefinition
        {
            Id = "flow-1",
            Enabled = true,
            Fetches = [new FetchNodeDefinition { Id = "primary-fetch", Fetch = new ModuleStepDefinition { Module = "fake-fetch" } }],
            Parses = [new ParseNodeDefinition { Id = "primary", Parse = new ModuleStepDefinition { Module = "fake-parse" }, FetchNodeIds = ["primary-fetch"] }],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Delivers = [DeliverAction("deliver-fails")],
                    AtomicScope = "scope-1"
                },
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Delivers = [DeliverAction("unrelated-deliver")]
                }
            ]
        };
        var (runtime, retryStore, _) = CreateRuntime(catalog, pipeline);

        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync("flow-1", CancellationToken.None));

        Assert.Single(unrelatedDelivered);
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ResumeRetryAsync_SecondDeliverOfRouteRetried_ResumesThatDeliverWithoutRerunningEarlierOnes()
    {
        var firstDelivered = new List<IntegrationBatch>();
        var secondDelivered = new List<IntegrationBatch>();
        var thirdDelivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(DelegateModuleFactory.Fetch("fake-fetch", SinglePayloadBatch()))
            .AddParse(DelegateModuleFactory.PassthroughParse("fake-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("first-deliver", firstDelivered))
            .AddDeliver(DelegateModuleFactory.Deliver("second-deliver", secondDelivered, failOnCall: call => call == 1))
            .AddDeliver(DelegateModuleFactory.Deliver("third-deliver", thirdDelivered));

        var retryPolicy = new RetryPolicyDefinition { MaxAttempts = 2, Delay = TimeSpan.FromMilliseconds(1) };
        var pipeline = FanOutPipeline(
            null,
            DeliverAction("first-deliver"),
            DeliverAction("second-deliver", retry: retryPolicy),
            DeliverAction("third-deliver"));
        var (runtime, retryStore, _) = CreateRuntime(catalog, pipeline);

        var scheduled = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Retrying, scheduled.Outcome);
        var pendingState = Assert.Single(retryStore.States);
        Assert.Equal("delivery:0:deliver:1", pendingState.StageId);
        Assert.Single(firstDelivered);
        Assert.Empty(secondDelivered);
        Assert.Empty(thirdDelivered);

        var resumed = await runtime.ResumeRetryAsync(pendingState, CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.Equal(FlowExecutionOutcome.Completed, resumed!.Outcome);
        Assert.Single(firstDelivered);
        Assert.Single(secondDelivered);
        Assert.Single(thirdDelivered);
        Assert.Empty(retryStore.States);
    }

    private static PipelineDefinition ManyFetchesToOneParsePipeline(RetryPolicyDefinition? parseRetry = null)
        => new()
        {
            Id = "flow-1",
            Enabled = true,
            Fetches =
            [
                new FetchNodeDefinition { Id = "inbox-a", Fetch = new ModuleStepDefinition { Module = "fetch-a" } },
                new FetchNodeDefinition { Id = "inbox-b", Fetch = new ModuleStepDefinition { Module = "fetch-b" } },
                new FetchNodeDefinition { Id = "inbox-c", Fetch = new ModuleStepDefinition { Module = "fetch-c" } },
                new FetchNodeDefinition { Id = "customers-fetch", Fetch = new ModuleStepDefinition { Module = "fetch-customers" } }
            ],
            Parses =
            [
                new ParseNodeDefinition
                {
                    Id = "orders",
                    Parse = new ModuleStepDefinition { Module = "orders-parse", Retry = parseRetry },
                    FetchNodeIds = ["inbox-a", "inbox-b", "inbox-c"]
                },
                new ParseNodeDefinition
                {
                    Id = "customers",
                    Parse = new ModuleStepDefinition { Module = "customers-parse" },
                    FetchNodeIds = ["customers-fetch"]
                }
            ],
            Deliveries =
            [
                new DeliveryRouteDefinition
                {
                    Render = new ModuleStepDefinition { Module = "fake-render" },
                    Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "fake-deliver" } }]
                }
            ]
        };

    private static DelegateFetchModule CountingFetch(string id, string payloadName, Dictionary<string, int> fetchCalls)
        => new(id, (_, _, _, _) =>
        {
            fetchCalls[id] = fetchCalls.GetValueOrDefault(id) + 1;
            return Task.FromResult(SinglePayloadBatch(payloadName));
        });

    [Fact]
    public async Task ExecuteAsync_SeveralFetchesFeedingOneParse_ParsesEachRawBatchAndTagsMergedOutputWithParseId()
    {
        var fetchCalls = new Dictionary<string, int>();
        var parsedBatches = new List<string[]>();
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(CountingFetch("fetch-a", "a.json", fetchCalls))
            .AddFetch(CountingFetch("fetch-b", "b.json", fetchCalls))
            .AddFetch(CountingFetch("fetch-c", "c.json", fetchCalls))
            .AddFetch(CountingFetch("fetch-customers", "customers.json", fetchCalls))
            .AddParse(new DelegateParseModule("orders-parse", (_, batch, _, _) =>
            {
                parsedBatches.Add(batch.Payloads.Select(static payload => payload.Name).ToArray());
                return Task.FromResult(new IntegrationBatch(batch.Payloads.Select(static payload => payload with { Name = $"parsed-{payload.Name}" }).ToArray()));
            }))
            .AddParse(DelegateModuleFactory.PassthroughParse("customers-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("fake-deliver", delivered));

        var (runtime, retryStore, _) = CreateRuntime(catalog, ManyFetchesToOneParsePipeline());
        var result = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Completed, result.Outcome);
        // The shared parse module ran once per input fetch node, each call seeing only that fetch's raw batch.
        Assert.Equal([["a.json"], ["b.json"], ["c.json"]], parsedBatches);
        var deliveredBatch = Assert.Single(delivered);
        Assert.Equal(4, result.PayloadCount);
        Assert.Equal(
            ["parsed-a.json:orders", "parsed-b.json:orders", "parsed-c.json:orders", "customers.json:customers"],
            deliveredBatch.Payloads.Select(static payload => $"{payload.Name}:{payload.Metadata["sourceId"]}"));
        Assert.Equal(4, fetchCalls.Count);
        Assert.All(fetchCalls.Values, count => Assert.Equal(1, count));
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ResumeRetryAsync_ParseStageFailure_ResumesFromResolvedFetchesWithoutRefetching()
    {
        var fetchCalls = new Dictionary<string, int>();
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(CountingFetch("fetch-a", "a.json", fetchCalls))
            .AddFetch(CountingFetch("fetch-b", "b.json", fetchCalls))
            .AddFetch(CountingFetch("fetch-c", "c.json", fetchCalls))
            .AddFetch(CountingFetch("fetch-customers", "customers.json", fetchCalls))
            .AddParse(DelegateModuleFactory.PassthroughParse("orders-parse", failOnCall: call => call == 1))
            .AddParse(DelegateModuleFactory.PassthroughParse("customers-parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("fake-render"))
            .AddDeliver(DelegateModuleFactory.Deliver("fake-deliver", delivered));

        var retryPolicy = new RetryPolicyDefinition { MaxAttempts = 2, Delay = TimeSpan.FromMilliseconds(1) };
        var (runtime, retryStore, _) = CreateRuntime(catalog, ManyFetchesToOneParsePipeline(parseRetry: retryPolicy));

        var scheduled = await runtime.ExecuteAsync("flow-1", CancellationToken.None);

        Assert.Equal(FlowExecutionOutcome.Retrying, scheduled.Outcome);
        var pending = Assert.Single(retryStore.States);
        Assert.Equal("parse:orders", pending.StageId);
        Assert.Equal(3, pending.InputPayloads.Count);
        Assert.Equal(3, pending.ResolvedFetchPayloads.Keys.Intersect(["inbox-a", "inbox-b", "inbox-c"]).Count());

        var resumed = await runtime.ResumeRetryAsync(pending, CancellationToken.None);

        Assert.NotNull(resumed);
        Assert.Equal(FlowExecutionOutcome.Completed, resumed!.Outcome);
        Assert.Equal(1, fetchCalls["fetch-a"]);
        Assert.Equal(1, fetchCalls["fetch-b"]);
        Assert.Equal(1, fetchCalls["fetch-c"]);
        var deliveredBatch = Assert.Single(delivered);
        Assert.Equal(3, deliveredBatch.Payloads.Count(static payload => payload.Metadata["sourceId"] == "orders"));
        Assert.Single(deliveredBatch.Payloads, static payload => payload.Metadata["sourceId"] == "customers");
        Assert.Empty(retryStore.States);
    }

    [Fact]
    public async Task ExecuteAsync_FetchAndParseSharingAnId_ThrowsInvalidOperationException()
    {
        var catalog = new FakeExecutableModuleCatalog();
        var pipeline = new PipelineDefinition
        {
            Id = "flow-1",
            Enabled = true,
            Fetches = [new FetchNodeDefinition { Id = "primary", Fetch = new ModuleStepDefinition { Module = "fake-fetch" } }],
            Parses = [new ParseNodeDefinition { Id = "primary", Parse = new ModuleStepDefinition { Module = "fake-parse" }, FetchNodeIds = ["primary"] }]
        };
        var (runtime, _, _) = CreateRuntime(catalog, pipeline);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync("flow-1", CancellationToken.None));
        Assert.Contains("both a fetch node and a parse node", exception.Message);
    }

    [Fact]
    public async Task ExecuteAsync_CyclicSourceGraph_ThrowsInvalidOperationException()
    {
        var catalog = new FakeExecutableModuleCatalog();
        var pipeline = new PipelineDefinition
        {
            Id = "flow-1",
            Enabled = true,
            Fetches =
            [
                new FetchNodeDefinition { Id = "a-fetch", Fetch = new ModuleStepDefinition { Module = "fake-fetch" }, InputParseIds = ["b"] },
                new FetchNodeDefinition { Id = "b-fetch", Fetch = new ModuleStepDefinition { Module = "fake-fetch" }, InputParseIds = ["a"] }
            ],
            Parses =
            [
                new ParseNodeDefinition { Id = "a", Parse = new ModuleStepDefinition { Module = "fake-parse" }, FetchNodeIds = ["a-fetch"] },
                new ParseNodeDefinition { Id = "b", Parse = new ModuleStepDefinition { Module = "fake-parse" }, FetchNodeIds = ["b-fetch"] }
            ]
        };
        var (runtime, _, _) = CreateRuntime(catalog, pipeline);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.ExecuteAsync("flow-1", CancellationToken.None));
        Assert.Contains("cyclic source graph involving: a-fetch, b-fetch, a, b", exception.Message);
    }
}
