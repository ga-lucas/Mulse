using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Services;
using Service.Endpoints;

namespace Service.Tests;

public sealed class StorageSettingsTests
{
    [Theory]
    [InlineData("sQLite", "Sqlite", true)]
    [InlineData("sqlserver", "SqlServer", false)]
    [InlineData("POSTGRESQL", "PostgreSql", false)]
    [InlineData("Json", "Json", false)]
    public async Task UiClient_ReadsEffectiveStartupSettingsWithoutConnectionStringsOrKeys(string configuredProvider, string expectedProvider, bool expectedMigrations)
    {
        var root = Path.Combine(Path.GetTempPath(), "mulse-storage-settings-" + Guid.NewGuid());
        var connection = expectedProvider == "Sqlite"
            ? "Data Source=Data\\workspace.db;Password=connection-password-sentinel"
            : expectedProvider == "PostgreSql"
                ? "Host=private-host-sentinel;Database=private-db-sentinel;Username=private-user-sentinel;Password=connection-password-sentinel"
                : "Server=private-host-sentinel;Database=private-db-sentinel;User Id=private-user-sentinel;Password=connection-password-sentinel";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = configuredProvider,
            ["ConnectionStrings:Mulse"] = connection,
            ["Mulse:RuntimeStatePath"] = "Data\\legacy.json",
            ["Persistence:Encryption:KeyId"] = "workspace-key-id",
            ["Mulse:Encryption:KeyEnvironmentVariable"] = "MULSE_TEST_EXTERNAL_KEY",
            ["Mulse:Encryption:KeyBase64"] = "inline-key-sentinel"
        }).Build();
        var registrations = new ServiceCollection();
        registrations.AddWorkspacePersistence(configuration, root);
        using var services = registrations.BuildServiceProvider();
        var settings = services.GetRequiredService<RuntimeStorageSettings>();
        configuration["Persistence:Provider"] = "Json";
        configuration["Persistence:ApplyMigrations"] = "true";
        configuration["Persistence:Encryption:KeyId"] = "edited-after-startup";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(settings);
        await using var app = builder.Build();
        app.MapStorageEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            var response = await client.GetStorageAsync(default);
            Assert.Equal(expectedProvider, response.Provider);
            Assert.Equal(expectedProvider != "Json", response.ConnectionConfigured);
            Assert.Equal(expectedMigrations, response.ApplyMigrations);
            Assert.Equal(expectedProvider == "Json" ? null : "workspace-key-id", response.EncryptionKeyId);
            Assert.Equal("MULSE_TEST_EXTERNAL_KEY", response.KeyEnvironmentVariable);
            Assert.Equal(expectedProvider switch
            {
                "Sqlite" => Path.Combine(root, "Data", "workspace.db"),
                "Json" => Path.Combine(root, "Data", "legacy.json"),
                _ => null
            }, response.DataFile);
            var json = await http.GetStringAsync("/api/workspace/storage");
            Assert.DoesNotContain("connection-password-sentinel", json);
            Assert.DoesNotContain("private-host-sentinel", json);
            Assert.DoesNotContain("private-db-sentinel", json);
            Assert.DoesNotContain("private-user-sentinel", json);
            Assert.DoesNotContain("inline-key-sentinel", json);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(11, document.RootElement.EnumerateObject().Count());
            Assert.False(response.CaptureStagePayloads);
            Assert.Equal(30, response.HistoryRetentionDays);
            Assert.Equal(90, response.ShutdownDrainSeconds);
            Assert.Equal(64, response.MaxConcurrentExecutions);
            Assert.Equal(10000, response.MaxPendingMessages);
            using var change = await http.PutAsync("/api/workspace/storage", new StringContent("{}"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, change.StatusCode);
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("Sqlite", false)]
    [InlineData("SqlServer", true)]
    [InlineData("PostgreSql", true)]
    public void MigrationPolicy_ReportsExplicitOverride(string provider, bool applyMigrations)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = provider,
            ["ConnectionStrings:Mulse"] = provider == "Sqlite" ? "Data Source=workspace.db" : "",
            ["Persistence:ApplyMigrations"] = applyMigrations.ToString()
        }).Build();
        configuration["ConnectionStrings:Mulse"] = provider switch
        {
            "Sqlite" => "Data Source=workspace.db",
            "SqlServer" => "Server=localhost;Database=workspace;Integrated Security=true",
            _ => "Host=localhost;Database=workspace;Username=test"
        };
        var registrations = new ServiceCollection();
        registrations.AddWorkspacePersistence(configuration, AppContext.BaseDirectory);
        using var services = registrations.BuildServiceProvider();
        Assert.Equal(applyMigrations, services.GetRequiredService<RuntimeStorageSettings>().ApplyMigrations);
    }

    [Fact]
    public void UnconfiguredConsumers_ReportLegacyDefaults()
    {
        var registrations = new ServiceCollection();
        registrations.AddWorkspacePersistence(new ConfigurationBuilder().Build(), AppContext.BaseDirectory);
        using var services = registrations.BuildServiceProvider();
        var settings = services.GetRequiredService<RuntimeStorageSettings>();
        Assert.Equal("Json", settings.Provider);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Data", "mulse-runtime.json"), settings.DataFile);
        Assert.False(settings.ConnectionConfigured);
        Assert.False(settings.ApplyMigrations);
        Assert.Null(settings.EncryptionKeyId);
        Assert.Equal("MULSE_STATE_ENCRYPTION_KEY", settings.KeyEnvironmentVariable);
    }
}
