using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Service.Tests;

public sealed class ProcessingHostTests
{
    [Fact]
    public async Task RealHost_ShutdownLifecycleWaitsForActiveFlowBeforeStoppingWorkers()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-host-drain-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory });
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Persistence:Provider"] = "Sqlite",
                ["ConnectionStrings:Mulse"] = "Data Source=" + Path.Combine(directory, "host.db") + ";Pooling=False",
                ["Mulse:RuntimeStatePath"] = Path.Combine(directory, "state.json"),
                ["Processing:ShutdownDrainSeconds"] = "5"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddMulsePlatform(builder.Configuration, directory);
            var inner = new WaitingRuntime();
            builder.Services.AddSingleton<IFlowRuntime>(services => new CoordinatedFlowRuntime(inner, services.GetRequiredService<ProcessingCoordinator>()));
            await using var app = builder.Build();
            await app.StartAsync();
            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            var run = app.Services.GetRequiredService<IFlowRuntime>().ExecuteAsync("test", lifetime.ApplicationStopping);
            await inner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var shutdown = app.StopAsync();
            Assert.False(shutdown.IsCompleted);
            Assert.False(inner.Token.IsCancellationRequested);
            Assert.False(app.Services.GetRequiredService<ProcessingCoordinator>().IsAccepting);
            inner.Finish.SetResult();
            await run;
            await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(app.Services.GetRequiredService<IRuntimeConfigurationStore>().GetState().MaintenanceRequested);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("Json")]
    [InlineData("Sqlite")]
    public async Task AbruptProcessExit_ReleasesWorkspaceOwnershipAndPreservesPersistentPause(string provider)
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-process-exit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.json");
        var databasePath = Path.Combine(directory, "workspace.db");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = provider,
            ["ConnectionStrings:Mulse"] = "Data Source=" + databasePath + ";Pooling=False",
            ["Mulse:RuntimeStatePath"] = path
        }).Build();
        var registrations = new ServiceCollection().AddLogging().AddSingleton<IHostEnvironment>(new FakeHostEnvironment());
        registrations.AddMulsePlatform(configuration, directory);
        try
        {
            var messageId = Guid.NewGuid();
            byte[]? acceptedPayload = null;
            using (var seed = registrations.BuildServiceProvider())
            {
                await seed.GetRequiredService<IRuntimeConfigurationStore>().SetMaintenanceAsync(true, default);
                if (provider == "Sqlite")
                {
                    var batch = new IntegrationBatch([new("pending-input", BinaryData.FromString("crash-retained-payload"), "text/plain", new Dictionary<string, string>())]);
                    await seed.GetRequiredService<FlowJournal>().EnqueueAsync("http:orders", "orders", messageId, batch, default);
                    using var db = seed.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Mulse.Persistence.MulseDbContext>>().CreateDbContext();
                    acceptedPayload = Assert.Single(db.Messages).Payload;
                }
            }
            for (var restart = 0; restart < 2; restart++)
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo("dotnet")
                    {
                        WorkingDirectory = directory,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false
                    }
                };
                process.StartInfo.ArgumentList.Add(typeof(ProcessingCoordinator).Assembly.Location);
                process.StartInfo.Environment["Authentication__Authority"] = "http://127.0.0.1:1/unused";
                process.StartInfo.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
                process.StartInfo.Environment["Persistence__Provider"] = provider;
                process.StartInfo.Environment["ConnectionStrings__Mulse"] = "Data Source=" + databasePath + ";Pooling=False";
                process.StartInfo.Environment["Mulse__RuntimeStatePath"] = path;
                process.StartInfo.Environment["Logging__LogLevel__Default"] = "Information";
                process.StartInfo.Environment["DOTNET_ENVIRONMENT"] = "Production";
                Assert.True(process.Start());
                var errors = process.StandardError.ReadToEndAsync();
                var output = new System.Text.StringBuilder();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    while (true)
                    {
                        var line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                        if (line is null) Assert.Fail("Service exited before listening: " + output + await errors);
                        output.AppendLine(line);
                        if (line.Contains("Now listening on:", StringComparison.Ordinal)) break;
                    }
                    Assert.Throws<InvalidOperationException>(() => new WorkspaceOwnership(provider == "Json" ? path : databasePath));
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(timeout.Token);
                    await errors;
                    using var reloaded = registrations.BuildServiceProvider();
                    Assert.True(reloaded.GetRequiredService<IRuntimeConfigurationStore>().GetState().MaintenanceRequested);
                    if (provider == "Sqlite")
                    {
                        using var db = reloaded.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Mulse.Persistence.MulseDbContext>>().CreateDbContext();
                        var message = Assert.Single(db.Messages);
                        Assert.Equal(messageId, message.Id);
                        Assert.Equal("Pending", message.State);
                        Assert.NotNull(acceptedPayload);
                        Assert.Equal(acceptedPayload, message.Payload);
                    }
                }
                finally
                {
                    if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
                }
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private sealed class WaitingRuntime : IFlowRuntime
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
        public Task<FlowExecutionResult?> ReleaseDueBatchAsync(string flowId, CancellationToken token) => Task.FromResult<FlowExecutionResult?>(null);
        public Task<FlowExecutionResult> ReleaseCollectedBatchAsync(string flowId, string instanceId, int count, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retry, CancellationToken token) => throw new NotSupportedException();
        public Task<FlowExecutionResult> RetrySourceOutcomesAsync(string flowId, CancellationToken token) => throw new NotSupportedException();
    }
}
