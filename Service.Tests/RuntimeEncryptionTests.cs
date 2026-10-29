using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mulse.Modules.Security;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class RuntimeEncryptionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mulse-encryption-" + Guid.NewGuid());
    private readonly string _variable = "MULSE_TEST_KEY_" + Guid.NewGuid().ToString("N");
    private string StatePath => Path.Combine(_directory, "state.json");

    private RuntimeConfigurationStore Store(IRuntimeStatePayloadProtector? protector = null)
        => new(Options.Create(new MulseOptions
        {
            RuntimeStatePath = StatePath,
            Encryption = new RuntimeStateEncryptionOptions { KeyEnvironmentVariable = _variable }
        }), new FakeHostEnvironment(), protector ?? NoOpRuntimeStatePayloadProtector.Instance);

    private void SetKey() => Environment.SetEnvironmentVariable(_variable, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    [Fact]
    public async Task Toggle_MigratesExistingStateAndSurvivesRestartsInBothDirections()
    {
        SetKey();
        var store = Store();
        await store.SetConfigValueAsync("credential", "sensitive-test-value", CancellationToken.None);
        await store.UpsertFlowAsync(new PipelineDefinition { Id = "preserved-flow" }, CancellationToken.None);
        Assert.False(store.GetEncryptionStatus().Enabled);
        Assert.True((await store.SetEncryptionAsync(true, CancellationToken.None)).Enabled);
        var encrypted = await File.ReadAllBytesAsync(StatePath);
        Assert.DoesNotContain("sensitive-test-value", Encoding.UTF8.GetString(encrypted));
        Assert.DoesNotContain(Environment.GetEnvironmentVariable(_variable)!, Encoding.UTF8.GetString(encrypted));
        var restarted = Store();
        Assert.True(restarted.GetEncryptionStatus().Enabled);
        Assert.Equal("sensitive-test-value", restarted.GetState().ConfigValues["credential"]);
        Assert.Equal("preserved-flow", restarted.GetState().Pipelines.Single().Id);
        await restarted.SetConfigValueAsync("added", "after-restart", CancellationToken.None);
        Assert.True(Store().GetEncryptionStatus().Enabled);
        Assert.Equal("after-restart", Store().GetState().ConfigValues["added"]);
        Assert.False((await restarted.SetEncryptionAsync(false, CancellationToken.None)).Enabled);
        var disabled = Store(new AesGcmRuntimeStatePayloadProtector(RandomNumberGenerator.GetBytes(32)));
        Assert.False(disabled.GetEncryptionStatus().Enabled);
        Assert.Equal("sensitive-test-value", disabled.GetState().ConfigValues["credential"]);
        Environment.SetEnvironmentVariable(_variable, null);
        Assert.Equal("after-restart", Store().GetState().ConfigValues["added"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-base64!")]
    [InlineData("YQ==")]
    public async Task MissingOrInvalidEnvironmentKey_RejectsBothDirectionsWithoutChangingState(string? key)
    {
        Environment.SetEnvironmentVariable(_variable, key);
        var store = Store();
        var original = await File.ReadAllBytesAsync(StatePath);
        Assert.False(store.GetEncryptionStatus().KeyAvailable);
        Assert.Contains(_variable, store.GetEncryptionStatus().KeyError!);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetEncryptionAsync(true, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SetEncryptionAsync(false, CancellationToken.None));
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.False(store.GetEncryptionStatus().Enabled);
    }

    [Fact]
    public async Task EncryptedState_MissingWrongOrCorruptedKeyNeverSilentlyReseeds()
    {
        SetKey();
        var key = Environment.GetEnvironmentVariable(_variable);
        var store = Store();
        await store.SetConfigValueAsync("credential", "sensitive-test-value", CancellationToken.None);
        await store.SetEncryptionAsync(true, CancellationToken.None);
        var original = await File.ReadAllBytesAsync(StatePath);
        Environment.SetEnvironmentVariable(_variable, null);
        Assert.Throws<InvalidOperationException>(() => Store());
        SetKey();
        Assert.Throws<AuthenticationTagMismatchException>(() => Store());
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Environment.SetEnvironmentVariable(_variable, key);
        Assert.Equal("sensitive-test-value", Store().GetState().ConfigValues["credential"]);
    }

    [Fact]
    public async Task FailedOrCancelledSwitch_KeepsPreviousDiskAndMemoryMode()
    {
        SetKey();
        var store = Store();
        var original = await File.ReadAllBytesAsync(StatePath);
        await using (var locked = new FileStream(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await Assert.ThrowsAnyAsync<IOException>(() => store.SetEncryptionAsync(true, CancellationToken.None));
        }
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.False(store.GetEncryptionStatus().Enabled);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SetEncryptionAsync(true, cancelled.Token));
        Assert.Equal(original, await File.ReadAllBytesAsync(StatePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        await store.SetEncryptionAsync(true, CancellationToken.None);
        Assert.True(Store().GetEncryptionStatus().Enabled);
    }

    [Fact]
    public async Task ConcurrentWritesAndSwitches_PreserveAllValuesAndRestartability()
    {
        SetKey();
        var store = Store();
        await Task.WhenAll(Enumerable.Range(0, 20).Select(async index =>
        {
            await store.SetConfigValueAsync($"key-{index}", $"value-{index}", CancellationToken.None);
            await store.SetEncryptionAsync(index % 2 == 0, CancellationToken.None);
        }));
        Assert.Equal(20, Store().GetState().ConfigValues.Count);
        Assert.Equal(store.GetEncryptionStatus().Enabled, Store().GetEncryptionStatus().Enabled);
    }

    [Fact]
    public async Task LegacyEncryptedState_RemainsReadableAndCanBeSwitchedToEnvironmentKey()
    {
        var legacyProtector = new AesGcmRuntimeStatePayloadProtector(RandomNumberGenerator.GetBytes(32));
        var store = Store(legacyProtector);
        await store.SetConfigValueAsync("legacy", "preserved", CancellationToken.None);
        Assert.Equal("preserved", Store(legacyProtector).GetState().ConfigValues["legacy"]);
        SetKey();
        await store.SetEncryptionAsync(true, CancellationToken.None);
        Assert.Equal("preserved", Store().GetState().ConfigValues["legacy"]);
    }

    [Fact]
    public async Task ProductionRegistration_SavedOffModeOverridesEnabledDefaultWithoutKey()
    {
        SetKey();
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Mulse:RuntimeStatePath"] = StatePath,
            ["Mulse:Encryption:Enabled"] = "true",
            ["Mulse:Encryption:KeyEnvironmentVariable"] = _variable
        }).Build();
        using (var services = new ServiceCollection().AddLogging()
            .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeHostEnvironment())
            .AddMulsePlatform(configuration, _directory).BuildServiceProvider())
        {
            var store = services.GetRequiredService<IRuntimeConfigurationStore>();
            Assert.True(store.GetEncryptionStatus().Enabled);
            await store.SetConfigValueAsync("preserved", "saved-before-disable", CancellationToken.None);
            await store.SetEncryptionAsync(false, CancellationToken.None);
        }
        Environment.SetEnvironmentVariable(_variable, null);
        using var restarted = new ServiceCollection().AddLogging()
            .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeHostEnvironment())
            .AddMulsePlatform(configuration, _directory).BuildServiceProvider();
        var reloaded = restarted.GetRequiredService<IRuntimeConfigurationStore>();
        Assert.False(reloaded.GetEncryptionStatus().Enabled);
        Assert.Equal("saved-before-disable", reloaded.GetState().ConfigValues["preserved"]);
    }

    [Fact]
    public async Task UiClient_UsesRealEndpoints_AndDiscoveryNeverReturnsValuesOrKey()
    {
        var store = Store();
        await store.SetConfigValueAsync("db-host", "private-host-value", CancellationToken.None);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(store);
        builder.Services.AddSingleton<IFlowDefinitionService>(new FakeFlowDefinitionService());
        builder.Services.AddSingleton<IConfigValueResolver>(new ConfigValueResolver(store));
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapEncryptionEndpoints();
        app.MapConfigValueEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            Assert.False((await client.GetEncryptionAsync(CancellationToken.None)).KeyAvailable);
            using var blocked = await http.PutAsJsonAsync("/api/workspace/encryption", new { Enabled = true });
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            SetKey();
            var status = await client.SetEncryptionAsync(true, CancellationToken.None);
            Assert.True(status.Enabled);
            Assert.True(status.KeyAvailable);
            Assert.Equal(new[] { "db-host" }, await client.GetConfigReferencesAsync(CancellationToken.None));
            var names = await http.GetStringAsync("/api/config-values/references");
            var metadata = await http.GetStringAsync("/api/workspace/encryption");
            Assert.DoesNotContain("private-host-value", names + metadata);
            Assert.DoesNotContain(Environment.GetEnvironmentVariable(_variable)!, names + metadata);
            Assert.False((await client.SetEncryptionAsync(false, CancellationToken.None)).Enabled);
        }
        finally { await app.StopAsync(); }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(_variable, null);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
