using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mulse.Persistence;
using Mulse.Ui.Services;
using Service.Endpoints;

namespace Service.Tests;

public sealed class OperationalControlTests
{
    [Theory]
    [InlineData("execute")]
    [InlineData("deadline")]
    [InlineData("release")]
    [InlineData("retry")]
    [InlineData("source-outcome")]
    [InlineData("reconcile")]
    public async Task PersistentPause_BlocksEveryRuntimeEntryPointBeforeDelegation(string operation)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        var store = fixture.Store();
        await store.SetMaintenanceAsync(true, default);
        using var lifetime = new ProcessingCoordinatorTests.TestLifetime();
        using var coordinator = new ProcessingCoordinator(store, lifetime, Options.Create(new ProcessingOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ProcessingCoordinator>.Instance);
        IFlowRuntime runtime = new CoordinatedFlowRuntime(new RejectRuntime(), coordinator);
        Func<Task> action = operation switch
        {
            "execute" => () => runtime.ExecuteAsync("orders", default),
            "deadline" => () => runtime.ReleaseDueBatchAsync("orders", default),
            "release" => () => runtime.ReleaseCollectedBatchAsync("orders", "instance", 1, default),
            "retry" => () => runtime.ResumeRetryAsync(new(), default),
            "source-outcome" => () => runtime.RetrySourceOutcomesAsync("orders", default),
            "reconcile" => () => runtime.ReconcileNativeEffectAsync("orders", "instance", "effect", "verified", null, default),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        await Assert.ThrowsAsync<AdmissionRejectedException>(action);
        Assert.Equal(0, coordinator.GetStatus().ActiveExecutions);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task Backlog_ReturnsExactMetadataCountersWithoutPayloads(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        fixture.Store();
        var database = new WorkspaceDatabase(fixture.Factory, new DatabasePayloadCodec("primary", fixture.KeyVariable), true);
        var store = new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = fixture.StatePath }), new FakeHostEnvironment(), database: database);
        await database.OperationsAsync(session =>
        {
            var key = WorkspaceDocument.Identity("execution");
            session.Db.Executions.Add(new() { Key = key, ExecutionId = "execution", State = "Held", Definition = System.Text.Encoding.UTF8.GetBytes("private-definition-sentinel") });
            foreach (var state in new[] { "Pending", "Pending", "Claimed", "Held", "Completed", "Abandoned" })
                session.Db.Messages.Add(new() { Id = Guid.NewGuid(), State = state, Payload = System.Text.Encoding.UTF8.GetBytes("private-message-sentinel") });
            foreach (var state in new[] { "Unknown", "Unknown", "InFlight", "Succeeded" })
                session.Db.Deliveries.Add(new() { Key = WorkspaceDocument.Identity(state, Guid.NewGuid().ToString()), Id = Guid.NewGuid(), ExecutionKey = key, State = state });
            return Task.FromResult(true);
        }, true, default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(database);
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(store);
        builder.Services.Configure<FlowHistoryOptions>(options => options.MaxPendingMessages = 17);
        builder.Services.AddSingleton<ProcessingCoordinator>();
        await using var app = builder.Build();
        app.MapProcessingEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            var response = await client.GetProcessingBacklogAsync(default);
            Assert.True(response.DurableInboxSupported);
            Assert.Equal(4, response.UnfinishedMessages);
            Assert.Equal(2, response.PendingMessages);
            Assert.Equal(1, response.ClaimedMessages);
            Assert.Equal(1, response.HeldMessages);
            Assert.Equal(1, response.HeldExecutions);
            Assert.Equal(2, response.UnknownDeliveries);
            Assert.Equal(1, response.InFlightDeliveries);
            Assert.Equal(17, response.MaxPendingMessages);
            using var raw = await http.GetAsync("/api/workspace/processing/backlog");
            Assert.True(raw.Headers.CacheControl!.NoStore);
            var json = await raw.Content.ReadAsStringAsync();
            Assert.DoesNotContain("private-definition-sentinel", json);
            Assert.DoesNotContain("private-message-sentinel", json);
        }
        finally { await app.StopAsync(); }
    }

    [Theory]
    [InlineData("Processing:ShutdownDrainSeconds", "0")]
    [InlineData("Processing:ShutdownDrainSeconds", "3601")]
    [InlineData("Processing:MaxConcurrentExecutions", "0")]
    [InlineData("Processing:MaxConcurrentExecutions", "10001")]
    [InlineData("Persistence:History:MaxPendingMessages", "0")]
    [InlineData("Persistence:History:MaxPendingMessages", "1000001")]
    public void InvalidCapacityOrDrainOptions_AreRejected(string key, string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [key] = value }).Build();
        var registrations = new ServiceCollection().AddLogging();
        registrations.AddMulsePlatform(config, AppContext.BaseDirectory);
        using var services = registrations.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() =>
        {
            if (key.StartsWith("Processing:", StringComparison.Ordinal)) _ = services.GetRequiredService<IOptions<ProcessingOptions>>().Value;
            else _ = services.GetRequiredService<IOptions<FlowHistoryOptions>>().Value;
        });
    }

    private sealed class RejectRuntime : IFlowRuntime
    {
        public IReadOnlyList<PipelineDefinition> GetConfiguredFlows() => [];
        public Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken token) => throw new InvalidOperationException("Paused runtime was invoked.");
        public Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flowId, CancellationToken token) => throw new InvalidOperationException("Paused runtime was invoked.");
        public Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flowId, string instanceId, int count, CancellationToken token) => throw new InvalidOperationException("Paused runtime was invoked.");
        public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retry, CancellationToken token) => throw new InvalidOperationException("Paused runtime was invoked.");
        public Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken token) => throw new InvalidOperationException("Paused runtime was invoked.");
        public Task<FlowExecutionResult> ReconcileNativeEffectAsync(string flowId, string instanceId, string effectId, string evidence, IntegrationBatch? output, CancellationToken token)
            => throw new InvalidOperationException("Paused runtime was invoked.");
    }
}
