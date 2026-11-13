using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.CompatibilityPack;
using Mulse.Modules.BuiltIn.Fetch;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;
using Service.Triggers;

namespace Service.Tests;

public sealed class OperationalPersistenceTests
{
    private static IntegrationBatch Batch(string value) => new([new("message", BinaryData.FromString(value), "text/plain", new Dictionary<string, string>())]);

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task HttpAcceptance_CommitsBefore202_AndRestartedRuntimeFinalizesSourceExactlyOnce(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal(encrypted: true);
        var route = "orders-" + Guid.NewGuid().ToString("N");
        var registry = new HttpInboundRegistry();
        var source = new HttpInboundFetchModule(registry, NullLogger<HttpInboundFetchModule>.Instance, pair.Journal);
        var step = new ModuleStepDefinition { Module = "http-inbound-fetch", Settings = new() { ["route"] = route } };
        await using var subscription = await source.RegisterTriggerAsync(new("orders", "http-inbound-fetch", _ => { }), step, default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddSingleton<IHttpInboundRegistry>(registry);
        builder.Services.AddSingleton(pair.Journal);
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapHttpInboundEndpoints();
        app.MapHistoryEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            http.DefaultRequestHeaders.Add("Idempotency-Key", "one-request");
            using var accepted = await http.PostAsync("/api/inbound/" + route, new StringContent("accepted-before-restart"));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
            using var duplicate = await http.PostAsync("/api/inbound/" + route, new StringContent("accepted-before-restart"));
            Assert.Equal(HttpStatusCode.Accepted, duplicate.StatusCode);
            using var conflict = await http.PostAsync("/api/inbound/" + route, new StringContent("different-body"));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using (var db = fixture.Factory.CreateDbContext())
            {
                var message = Assert.Single(db.Messages);
                Assert.Equal("Pending", message.State);
                Assert.Equal("orders", message.FlowId);
                Assert.Null(message.ExecutionId);
            }
            var restarted = fixture.Journal();
            await restarted.Journal.RecoverAsync(default);
            var received = new List<IntegrationBatch>();
            var catalog = new FakeExecutableModuleCatalog()
                .AddFetch(new HttpInboundFetchModule(registry, NullLogger<HttpInboundFetchModule>.Instance, restarted.Journal))
                .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
                .AddRender(DelegateModuleFactory.PassthroughRender("render"))
                .AddDeliver(DelegateModuleFactory.Deliver("deliver", received));
            var flow = new PipelineDefinition
            {
                Id = "orders", Enabled = true,
                Fetches = [new() { Id = "source", Fetch = step }],
                Parses = [new() { Id = "parsed", FetchNodeIds = ["source"], Parse = new() { Module = "parse" } }],
                Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }]
            };
            var runtime = new FlowRuntime(new FakeFlowDefinitionService(flow), catalog, new FlowOrchestrationStateStore(restarted.Store),
                new FlowRetryStateStore(restarted.Store), new ConfigValueResolver(restarted.Store), TimeProvider.System, NullLogger<FlowRuntime>.Instance, restarted.Journal);
            Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ExecuteAsync("orders", default)).Outcome);
            Assert.Equal("accepted-before-restart", Assert.Single(Assert.Single(received).Payloads).GetText());
            using (var db = fixture.Factory.CreateDbContext())
            {
                Assert.Equal("Completed", Assert.Single(db.Messages).State);
                Assert.Equal("Completed", Assert.Single(db.Executions).State);
                Assert.Equal("Succeeded", Assert.Single(db.Deliveries).State);
                Assert.Empty(db.Retries);
                Assert.NotEmpty(db.ExecutionEvents);
            }
            // A fresh history service loads committed records, without returning payloads in list/detail views.
            var clientBuilder = WebApplication.CreateBuilder();
            clientBuilder.WebHost.UseUrls("http://127.0.0.1:0");
            clientBuilder.Logging.ClearProviders();
            clientBuilder.Services.AddSingleton(fixture.Journal().Journal);
            clientBuilder.Services.AddProblemDetails();
            clientBuilder.Services.AddExceptionHandler<ApiExceptionHandler>();
            await using var historyApp = clientBuilder.Build();
            historyApp.UseExceptionHandler();
            historyApp.MapHistoryEndpoints();
            await historyApp.StartAsync();
            try
            {
                var historyAddress = historyApp.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var historyHttp = new HttpClient { BaseAddress = new Uri(historyAddress) };
                var client = new MulseApiClient(historyHttp);
                var run = Assert.Single(await client.GetHistoryExecutionsAsync("ORDERS", default));
                Assert.Equal("Completed", run.State);
                var detail = await client.GetHistoryExecutionAsync(run.Id, default);
                Assert.Single(detail.Messages);
                Assert.Single(detail.Deliveries);
                using var list = await historyHttp.GetAsync("/api/history/executions");
                Assert.True(list.Headers.CacheControl?.NoStore);
                Assert.DoesNotContain("accepted-before-restart", await list.Content.ReadAsStringAsync());
                using var invalidLimit = await historyHttp.GetAsync("/api/history/executions?take=0");
                Assert.Equal(HttpStatusCode.BadRequest, invalidLimit.StatusCode);
                using var missing = await historyHttp.GetAsync("/api/history/executions/missing");
                Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            }
            finally { await historyApp.StopAsync(); }
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task FlowLink_QueuedBeforeSubscriberSurvivesRestartAndAcknowledgesOnlyAfterTerminalOutcome(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var first = fixture.Journal();
        await first.Journal.EnqueueAsync("flow-link:handoff", null, Guid.NewGuid(), Batch("durable-handoff"), default);
        var next = fixture.Journal();
        var source = new FlowLinkFetchModule(next.Journal);
        var context = new FlowExecutionContext("consumer", "consume-one", DateTimeOffset.UtcNow);
        var step = new ModuleStepDefinition { Module = "flow-link-fetch", Settings = new() { ["channel"] = "handoff" } };
        await next.Journal.BeginAsync(new() { Id = "consumer" }, context, default);
        var batch = await source.FetchAsync(context, IntegrationBatch.Empty, step, default);
        Assert.Equal("durable-handoff", Assert.Single(batch.Payloads).GetText());
        Assert.NotNull(batch.SourceReceipt);
        var receipt = new FetchProcessingReceipt("receipt", "source", step, new Dictionary<string, string>(batch.SourceReceipt));
        using (var db = fixture.Factory.CreateDbContext()) Assert.Equal("Claimed", Assert.Single(db.Messages).State);
        await source.OnProcessingCompletedAsync(context, receipt, new(true), default);
        await source.OnProcessingCompletedAsync(context, receipt, new(true), default);
        using (var db = fixture.Factory.CreateDbContext()) Assert.Equal("Completed", Assert.Single(db.Messages).State);
        Assert.Empty((await source.FetchAsync(new("consumer", "consume-two", DateTimeOffset.UtcNow), IntegrationBatch.Empty, step, default)).Payloads);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task HeldExecution_CanOnlyCloseWithoutReplayAfterReviewAndWithoutPendingContinuation(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var context = new FlowExecutionContext("orders", "held", DateTimeOffset.UtcNow);
        await pair.Journal.BeginAsync(new() { Id = "orders" }, context, default);
        await pair.Journal.EnqueueAsync("http:held", "orders", Guid.NewGuid(), Batch("must-not-replay"), default);
        await pair.Journal.ClaimAsync("http:held", context, default);
        await pair.Journal.RecoverAsync(default);
        await Assert.ThrowsAsync<ArgumentException>(() => pair.Journal.CloseHeldAsync("held", "guess", default));
        await pair.Store.UpsertRetryStateAsync(new() { FlowId = "orders", ExecutionId = "held", StageId = "fetch:f" }, default);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => pair.Journal.CloseHeldAsync("held", "Reviewed all available destination evidence.", default));
        await pair.Store.DeleteAllRetryStateForExecutionAsync("orders", "held", default);
        await pair.Journal.CloseHeldAsync("held", "Operator reviewed outcomes and explicitly abandoned the held input.", default);
        Assert.False(await pair.Journal.IsBlockedAsync("orders", default));
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal("Closed", Assert.Single(db.Executions).State);
        Assert.Equal("Abandoned", Assert.Single(db.Messages).State);
        Assert.Empty((await pair.Journal.ClaimAsync("http:held", new("orders", "new-run", DateTimeOffset.UtcNow), default)).Payloads);
        await Assert.ThrowsAsync<FlowDurabilityException>(() => pair.Journal.CloseHeldAsync("held", "Try to close it again without inspection.", default));
    }

    private static PipelineDefinition DeliveryPipeline() => new()
    {
        Id = "orders", Enabled = true,
        Fetches = [new() { Id = "f", Fetch = new() { Module = "fetch" } }],
        Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
        Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "deliver" } }] }]
    };

    private static FlowRuntime Runtime(PipelineDefinition flow, FakeExecutableModuleCatalog catalog, (RuntimeConfigurationStore Store, FlowJournal Journal) pair)
        => new(new FakeFlowDefinitionService(flow), catalog, new FlowOrchestrationStateStore(pair.Store), new FlowRetryStateStore(pair.Store),
            new ConfigValueResolver(pair.Store), TimeProvider.System, NullLogger<FlowRuntime>.Instance, pair.Journal);

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task PreparedDeliveryContinuation_RestartsWithoutRefetchingOrRendering(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var flow = DeliveryPipeline();
        var context = new FlowExecutionContext("orders", "prepared", DateTimeOffset.UtcNow);
        var batch = Batch("already-rendered");
        await pair.Journal.BeginAsync(flow, context, default);
        await pair.Store.UpsertRetryStateAsync(new()
        {
            FlowId = "orders", ExecutionId = "prepared", StageId = "delivery:0:deliver:0", PipelineSnapshot = flow,
            ExecutionStartedAt = context.StartedAt, NextAttemptAt = context.StartedAt,
            InputPayloads = batch.Payloads.Select(FlowOrchestrationPayloadSnapshot.Capture).ToList(),
            PostAugmentPayloads = batch.Payloads.Select(FlowOrchestrationPayloadSnapshot.Capture).ToList()
        }, default);
        await pair.Journal.PrepareDeliveryAsync(context, "delivery:0:deliver:0", flow.Deliveries[0].Delivers[0].Deliver, batch, default);
        var restarted = fixture.Journal();
        await restarted.Journal.RecoverAsync(default);
        var delivered = new List<IntegrationBatch>();
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("fetch", (_, _, _, _) => throw new InvalidOperationException("Must not refetch.")))
            .AddParse(new DelegateParseModule("parse", (_, _, _, _) => throw new InvalidOperationException("Must not reparse.")))
            .AddRender(new DelegateRenderModule("render", (_, _, _, _) => throw new InvalidOperationException("Must not rerender.")))
            .AddDeliver(DelegateModuleFactory.Deliver("deliver", delivered));
        var retry = Assert.Single(restarted.Store.GetState().PendingRetries);
        Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(flow, catalog, restarted).ResumeRetryAsync(retry, default))!.Outcome);
        Assert.Equal("already-rendered", Assert.Single(Assert.Single(delivered).Payloads).GetText());
        Assert.Empty(restarted.Store.GetState().PendingRetries);
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal("Succeeded", Assert.Single(db.Deliveries).State);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task LostDeliverySuccessCommitAcknowledgement_RestartSkipsCommittedEffect(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var flow = DeliveryPipeline();
        var calls = 0;
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("fetch", (_, _, _, _) => Task.FromResult(Batch("original"))))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(new DelegateDeliverModule("deliver", (_, _, _, _) =>
            {
                calls++;
                fixture.CommitFailure.Fail = true;
                return Task.CompletedTask;
            }));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Runtime(flow, catalog, pair).ExecuteAsync("orders", default));
        fixture.CommitFailure.Fail = false;
        using (var db = fixture.Factory.CreateDbContext()) Assert.Equal("Succeeded", Assert.Single(db.Deliveries).State);
        var restarted = fixture.Journal();
        await restarted.Journal.RecoverAsync(default);
        var retry = Assert.Single(restarted.Store.GetState().PendingRetries);
        Assert.Equal(FlowExecutionOutcome.Completed, (await Runtime(flow, catalog, restarted).ResumeRetryAsync(retry, default))!.Outcome);
        Assert.Equal(1, calls);
        Assert.Empty(restarted.Store.GetState().PendingRetries);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task LostInboxCommitAcknowledgement_NeverReturnsSuccessAndFreshProcessFindsCommittedMessage(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        var id = Guid.NewGuid();
        fixture.CommitFailure.Fail = true;
        await Assert.ThrowsAsync<FlowDurabilityException>(() => pair.Journal.EnqueueAsync("http:orders", "orders", id, Batch("saved"), default));
        Assert.Throws<InvalidOperationException>(() => pair.Store.GetState());
        fixture.CommitFailure.Fail = false;
        var restarted = fixture.Journal();
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal(id, Assert.Single(db.Messages).Id);
        Assert.Single(await restarted.Journal.PendingChannelsAsync(default));
    }
}
