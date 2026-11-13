using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules.Security;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class ProcessingCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-maintenance-" + Guid.NewGuid().ToString("N"));
    private RuntimeConfigurationStore Store() => new(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(_directory, "state.json") }),
        new FakeHostEnvironment(), NoOpRuntimeStatePayloadProtector.Instance);
    private static ProcessingCoordinator Coordinator(IRuntimeConfigurationStore store, TestLifetime lifetime, int seconds = 5, int limit = 64)
        => new(store, lifetime, Options.Create(new ProcessingOptions { ShutdownDrainSeconds = seconds, MaxConcurrentExecutions = limit }), NullLogger<ProcessingCoordinator>.Instance);

    [Fact]
    public async Task Maintenance_ClosesAdmissionDrainsExistingWorkAndPersistsAcrossRestart()
    {
        var store = Store();
        using var lifetime = new TestLifetime();
        using var coordinator = Coordinator(store, lifetime);
        var active = coordinator.Enter(true);
        var inbound = coordinator.Enter(false);
        Assert.Equal("Running", coordinator.GetStatus().State);
        var status = await coordinator.SetMaintenanceAsync(true, default);
        Assert.Equal("Draining", status.State);
        Assert.Equal(1, status.ActiveExecutions);
        Assert.Equal(1, status.ActiveAdmissions);
        Assert.Throws<AdmissionRejectedException>(() => coordinator.Enter(true));
        Assert.Throws<AdmissionRejectedException>(() => coordinator.Enter(false));
        Assert.False(coordinator.ForcedCancellation.IsCancellationRequested);
        var drained = coordinator.WaitForDrainAsync(default);
        active.Dispose();
        active.Dispose();
        Assert.False(drained.IsCompleted);
        inbound.Dispose();
        await drained.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("Paused", coordinator.GetStatus().State);
        using var restarted = Coordinator(Store(), lifetime);
        Assert.Equal("Paused", restarted.GetStatus().State);
        Assert.True(Store().GetState().MaintenanceRequested);
        await restarted.SetMaintenanceAsync(false, default);
        Assert.True(restarted.IsAccepting);
        Assert.False(Store().GetState().MaintenanceRequested);
    }

    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task DatabaseMaintenance_PersistsWithoutChangingPendingWork(string provider)
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var pair = fixture.Journal();
        await pair.Journal.EnqueueAsync("http:orders", "orders", Guid.NewGuid(), IntegrationBatch.Empty, default);
        await pair.Store.UpsertRetryStateAsync(new() { FlowId = "orders", ExecutionId = "retry", StageId = "fetch:f" }, default);
        using var lifetime = new TestLifetime();
        using (var coordinator = Coordinator(pair.Store, lifetime)) await coordinator.SetMaintenanceAsync(true, default);
        var reloaded = fixture.Journal();
        using var restarted = Coordinator(reloaded.Store, lifetime);
        Assert.False(restarted.IsAccepting);
        Assert.Single(reloaded.Store.GetState().PendingRetries);
        using var db = fixture.Factory.CreateDbContext();
        Assert.Equal("Pending", Assert.Single(db.Messages).State);
        Assert.True(Assert.Single(db.Workspaces).MaintenanceRequested);
    }

    [Fact]
    public async Task Shutdown_DrainsWithoutCancellingAdmittedRunOrPersistingMaintenance()
    {
        var store = Store();
        using var lifetime = new TestLifetime();
        using var coordinator = Coordinator(store, lifetime);
        var inner = new BlockingRuntime();
        var runtime = new CoordinatedFlowRuntime(inner, coordinator);
        using var worker = new CancellationTokenSource();
        var run = runtime.ExecuteAsync("orders", worker.Token);
        await inner.Started.Task;
        lifetime.StopApplication();
        var shutdown = coordinator.StoppingAsync(default);
        worker.Cancel();
        Assert.False(inner.Token.IsCancellationRequested);
        Assert.False(shutdown.IsCompleted);
        Assert.Throws<AdmissionRejectedException>(() => coordinator.Enter(true));
        inner.Finish.SetResult();
        await run;
        await shutdown;
        Assert.Equal(0, coordinator.GetStatus().ActiveExecutions);
        Assert.False(Store().GetState().MaintenanceRequested);
        using var freshLifetime = new TestLifetime();
        using var restarted = Coordinator(Store(), freshLifetime);
        Assert.True(restarted.IsAccepting);
    }

    [Fact]
    public async Task Shutdown_ExpiredBudgetCancelsWithoutReportingSuccessfulDrain()
    {
        using var lifetime = new TestLifetime();
        using var coordinator = Coordinator(Store(), lifetime, seconds: 1);
        var inner = new BlockingRuntime();
        var runtime = new CoordinatedFlowRuntime(inner, coordinator);
        var run = runtime.ExecuteAsync("orders", default);
        await inner.Started.Task;
        lifetime.StopApplication();
        await coordinator.StoppingAsync(default);
        Assert.True(coordinator.ForcedCancellation.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal("Stopping", coordinator.GetStatus().State);
        Assert.False(coordinator.IsAccepting);
    }

    [Fact]
    public async Task CallerCancellationOutsideShutdownStillCancels_AndCapacityIsBounded()
    {
        using var lifetime = new TestLifetime();
        using var coordinator = Coordinator(Store(), lifetime, limit: 1);
        var inner = new BlockingRuntime();
        var runtime = new CoordinatedFlowRuntime(inner, coordinator);
        using var request = new CancellationTokenSource();
        var run = runtime.ExecuteAsync("orders", request.Token);
        await inner.Started.Task;
        await Assert.ThrowsAsync<AdmissionRejectedException>(() => runtime.ReleaseDueBatchAsync("orders", default));
        request.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(0, coordinator.GetStatus().ActiveExecutions);
    }

    [Fact]
    public async Task PausePersistenceFailure_FailsClosedUntilRestart()
    {
        var store = Store();
        await store.SetMaintenanceAsync(false, default);
        using var lifetime = new TestLifetime();
        using var coordinator = Coordinator(store, lifetime);
        using var locked = new FileStream(Path.Combine(_directory, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAnyAsync<IOException>(() => coordinator.SetMaintenanceAsync(true, default));
        Assert.Equal("Faulted", coordinator.GetStatus().State);
        Assert.False(coordinator.IsAccepting);
        Assert.Throws<AdmissionRejectedException>(() => coordinator.Enter(true));
    }

    [Fact]
    public async Task InboundDuringMaintenance_Returns503RetryAfterWithoutReadingOrQueuingBody()
    {
        using var lifetime = new TestLifetime();
        using var coordinator = Coordinator(Store(), lifetime);
        await coordinator.SetMaintenanceAsync(true, default);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddSingleton(coordinator);
        builder.Services.AddSingleton<IHttpInboundRegistry, RejectRegistry>();
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapHttpInboundEndpoints();
        app.MapProcessingEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            using var rejected = await http.PostAsync("/api/inbound/orders", new StringContent("must-not-be-accepted"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, rejected.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(5), rejected.Headers.RetryAfter?.Delta);
            var client = new Mulse.Ui.Services.MulseApiClient(http);
            var status = await client.GetProcessingAsync(default);
            Assert.Equal("Paused", status.State);
            var resumed = await client.SetMaintenanceAsync(false, default);
            Assert.Equal("Running", resumed.State);
            Assert.False(resumed.MaintenanceRequested);
            Assert.True(coordinator.IsAccepting);
        }
        finally { await app.StopAsync(); }
    }

    private sealed class RejectRegistry : IHttpInboundRegistry
    {
        public bool TryGetRoute(string routeKey, out IHttpInboundRoute route) => throw new InvalidOperationException("Paused requests must be rejected before registry lookup.");
        public IDisposable RegisterRoute(string routeKey, IHttpInboundRoute route) => throw new NotSupportedException();
    }

    internal sealed class TestLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }

    private sealed class BlockingRuntime : IFlowRuntime
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Finish { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public IReadOnlyList<PipelineDefinition> GetConfiguredFlows() => [];
        public async Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken token)
        {
            Token = token;
            Started.TrySetResult();
            await Finish.Task.WaitAsync(token);
            return new(flowId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, [], FlowExecutionOutcome.Completed, null);
        }
        public Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flowId, CancellationToken token) => throw new InvalidOperationException("Admission should reject before delegation.");
        public Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flowId, string instanceId, int count, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retry, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken token) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
