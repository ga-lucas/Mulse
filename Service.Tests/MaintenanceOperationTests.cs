using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Service.Tests;

public sealed class MaintenanceOperationTests
{
    [Fact]
    public async Task MaintenanceOperation_RequiresDrainedPauseAndSerializesResume()
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var lifetime = new ProcessingCoordinatorTests.TestLifetime();
        using var coordinator = new ProcessingCoordinator(fixture.Store(), lifetime, Options.Create(new ProcessingOptions()), NullLogger<ProcessingCoordinator>.Instance);
        await Assert.ThrowsAsync<AdmissionRejectedException>(() => coordinator.EnterMaintenanceOperationAsync(default));
        var active = coordinator.Enter(true);
        await coordinator.SetMaintenanceAsync(true, default);
        await Assert.ThrowsAsync<AdmissionRejectedException>(() => coordinator.EnterMaintenanceOperationAsync(default));
        active.Dispose();
        var migration = await coordinator.EnterMaintenanceOperationAsync(default);
        try
        {
            Assert.Equal(1, coordinator.GetStatus().ActiveAdmissions);
            Assert.False(coordinator.IsAccepting);
            var resume = coordinator.SetMaintenanceAsync(false, default);
            Assert.False(resume.IsCompleted);
            migration.Dispose();
            migration.Dispose();
            Assert.Equal("Running", (await resume.WaitAsync(TimeSpan.FromSeconds(2))).State);
            Assert.Equal(0, coordinator.GetStatus().ActiveAdmissions);
        }
        finally { migration.Dispose(); }
    }

    [Fact]
    public async Task Shutdown_WaitsForMaintenanceCopyAndRejectsQueuedResume()
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var lifetime = new ProcessingCoordinatorTests.TestLifetime();
        using var coordinator = new ProcessingCoordinator(fixture.Store(), lifetime, Options.Create(new ProcessingOptions { ShutdownDrainSeconds = 5 }), NullLogger<ProcessingCoordinator>.Instance);
        await coordinator.SetMaintenanceAsync(true, default);
        using var operation = await coordinator.EnterMaintenanceOperationAsync(default);
        var resume = coordinator.SetMaintenanceAsync(false, default);
        lifetime.StopApplication();
        var shutdown = coordinator.StoppingAsync(default);
        Assert.False(shutdown.IsCompleted);
        Assert.False(coordinator.ForcedCancellation.IsCancellationRequested);
        operation.Dispose();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<AdmissionRejectedException>(() => resume);
        Assert.True(fixture.Store().GetState().MaintenanceRequested);
        Assert.Equal(0, coordinator.GetStatus().ActiveAdmissions);
    }

    [Fact]
    public async Task ShutdownBudgetExpiry_CancelsCopyAndCancelledWaitersDoNotLeakAdmissions()
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var lifetime = new ProcessingCoordinatorTests.TestLifetime();
        using var coordinator = new ProcessingCoordinator(fixture.Store(), lifetime, Options.Create(new ProcessingOptions { ShutdownDrainSeconds = 1 }), NullLogger<ProcessingCoordinator>.Instance);
        await coordinator.SetMaintenanceAsync(true, default);
        using var operation = await coordinator.EnterMaintenanceOperationAsync(default);
        using var waiting = new CancellationTokenSource();
        var second = coordinator.EnterMaintenanceOperationAsync(waiting.Token);
        waiting.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(1, coordinator.GetStatus().ActiveAdmissions);
        lifetime.StopApplication();
        await coordinator.StoppingAsync(default);
        Assert.True(coordinator.ForcedCancellation.IsCancellationRequested);
        operation.Dispose();
        Assert.Equal(0, coordinator.GetStatus().ActiveAdmissions);
    }
}
