using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Persistence;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class StorageMigrationEndpointTests
{
    [Theory]
    [MemberData(nameof(WorkspacePersistenceTests.Providers), MemberType = typeof(WorkspacePersistenceTests))]
    public async Task Wizard_RequiresDrainedPauseAndConfirmation_ThenSupportsReadOnlySourceRestart(string provider)
    {
        using var source = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        using var target = new WorkspacePersistenceTests.DatabaseFixture(provider);
        var store = source.Store();
        await store.SetConfigValueAsync("secret", "migration-private-value", default);
        target.Store();
        using (var context = target.Factory.CreateDbContext()) await context.Workspaces.ExecuteDeleteAsync();
        using var sourceContext = source.Factory.CreateDbContext();
        using var targetContext = target.Factory.CreateDbContext();
        var config = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["ConnectionStrings:Mulse"] = sourceContext.Database.GetConnectionString(),
            ["Mulse:RuntimeStatePath"] = source.StatePath,
            ["Mulse:Encryption:KeyEnvironmentVariable"] = source.KeyVariable
        };
        await using (var app = Build(source.DirectoryPath, config))
        {
            await app.StartAsync();
            try
            {
                using var http = Client(app);
                var client = new MulseApiClient(http);
                var status = await client.GetStorageMigrationAsync(default);
                Assert.True(status.Supported);
                var request = new StorageMigrationRequestViewModel(provider, targetContext.Database.GetConnectionString()!, false, Guid.NewGuid());
                using (var running = await http.PostAsJsonAsync("/api/workspace/storage/migration/preview", request))
                    Assert.Equal(HttpStatusCode.ServiceUnavailable, running.StatusCode);
                await client.SetMaintenanceAsync(true, default);
                var preview = await client.RunStorageMigrationAsync(request, false, default);
                Assert.Equal(11, preview.Tables.Count);
                var execute = request with { ExpectedSourceVersion = preview.SourceVersion };
                using (var unconfirmed = await http.PostAsJsonAsync("/api/workspace/storage/migration/execute", execute))
                    Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
                execute = execute with { Confirmation = "MIGRATE STORAGE", BackupConfirmed = true };
                var result = await client.RunStorageMigrationAsync(execute, true, default);
                Assert.True(result.Completed);
                Assert.Equal(preview.Tables, result.Tables);
                Assert.Equal("MigrationLocked", (await client.GetProcessingAsync(default)).State);
                Assert.Equal("SourceSealed", (await client.GetStorageMigrationAsync(default)).State);
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetMaintenanceAsync(false, default));
                using var response = await http.GetAsync("/api/workspace/storage/migration");
                Assert.True(response.Headers.CacheControl!.NoStore);
                var json = await response.Content.ReadAsStringAsync();
                Assert.DoesNotContain("migration-private-value", json);
                Assert.DoesNotContain(targetContext.Database.GetConnectionString()!, json);
            }
            finally { await app.StopAsync(); }
        }
        await using var restarted = Build(source.DirectoryPath, config);
        await restarted.StartAsync();
        try
        {
            using var http = Client(restarted);
            var client = new MulseApiClient(http);
            Assert.Equal("MigrationLocked", (await client.GetProcessingAsync(default)).State);
            Assert.Equal("SourceSealed", (await client.GetStorageMigrationAsync(default)).State);
            await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.Services.GetRequiredService<IRuntimeConfigurationStore>().SetConfigValueAsync("forbidden", "write", default));
        }
        finally { await restarted.StopAsync(); }
    }

    [Fact]
    public async Task TargetCannotBeTheActiveSource_AndInvalidConnectionsAreNotEchoed()
    {
        using var fixture = new WorkspacePersistenceTests.DatabaseFixture("Sqlite");
        fixture.Store();
        using var db = fixture.Factory.CreateDbContext();
        var connection = db.Database.GetConnectionString()!;
        var settings = new Dictionary<string, string?> { ["Persistence:Provider"] = "Sqlite", ["ConnectionStrings:Mulse"] = connection, ["Mulse:RuntimeStatePath"] = fixture.StatePath };
        await using var app = Build(fixture.DirectoryPath, settings);
        await app.StartAsync();
        try
        {
            using var http = Client(app);
            var client = new MulseApiClient(http);
            await client.SetMaintenanceAsync(true, default);
            var request = new StorageMigrationRequestViewModel("Sqlite", connection, true, Guid.NewGuid());
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.RunStorageMigrationAsync(request, false, default));
            request = request with { Provider = "PostgreSql", ConnectionString = "unknown-secret-key=do-not-echo-credential" };
            using var response = await http.PostAsJsonAsync("/api/workspace/storage/migration/preview", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.DoesNotContain("do-not-echo-credential", await response.Content.ReadAsStringAsync());
            Assert.Equal("Paused", (await client.GetProcessingAsync(default)).State);
        }
        finally { await app.StopAsync(); }
    }

    private static WebApplication Build(string directory, Dictionary<string, string?> settings)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = directory });
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddMulsePlatform(builder.Configuration, directory);
        var app = builder.Build();
        app.UseExceptionHandler();
        app.MapStorageEndpoints();
        app.MapProcessingEndpoints();
        return app;
    }
    private static HttpClient Client(WebApplication app) => new()
    {
        BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single())
    };
}
