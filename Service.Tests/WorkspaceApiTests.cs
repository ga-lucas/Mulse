using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mulse.Modules;
using Mulse.Modules.Security;
using Mulse.Ui.Services;
using Service;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class WorkspaceApiTests
{
    [Fact]
    public async Task UiClient_BacksUpPreviewsRestoresDeletesAndControlsPackagesThroughRealEndpoints()
    {
        var directory = Path.Combine(Path.GetTempPath(), "mulse-workspace-api-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var store = new RuntimeConfigurationStore(Options.Create(new MulseOptions { RuntimeStatePath = Path.Combine(directory, "state.json"), PluginDirectories = ["plugins"] }),
                new FakeHostEnvironment { ContentRootPath = directory }, NoOpRuntimeStatePayloadProtector.Instance);
            await store.UpsertFlowAsync(new PipelineDefinition
            {
                Id = "original", Category = "API tests", Enabled = true,
                Fetches = [new FetchNodeDefinition { Id = "f", Fetch = new ModuleStepDefinition { Module = "fetch" } }],
                Parses = [new ParseNodeDefinition { Id = "p", FetchNodeIds = ["f"], Parse = new ModuleStepDefinition { Module = "parse" } }],
                Deliveries = [new DeliveryRouteDefinition { Render = new ModuleStepDefinition { Module = "render" }, Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "deliver" } }] }]
            }, CancellationToken.None);
            await store.SetConfigValueAsync("secret", "sensitive-value", CancellationToken.None);
            using var moduleServices = new ServiceCollection().AddLogging()
                .AddMulsePlatform(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), directory)
                .AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(new FakeHostEnvironment { ContentRootPath = directory })
                .AddSingleton<IRuntimeConfigurationStore>(store).BuildServiceProvider();
            var catalog = new ModuleCatalog(moduleServices, store, new FakeHostEnvironment { ContentRootPath = directory }, TimeProvider.System, NullLogger<ModuleCatalog>.Instance);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddProblemDetails();
            builder.Services.AddExceptionHandler<ApiExceptionHandler>();
            builder.Services.AddValidation();
            builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
            builder.Services.AddSingleton<IRuntimeConfigurationStore>(store);
            builder.Services.AddSingleton<IModuleCatalog>(catalog);
            builder.Services.AddSingleton<IFlowDefinitionService>(new FlowDefinitionService(store, catalog));
            builder.Services.AddSingleton<IFlowRuntime>(_ => throw new NotSupportedException("Execution is outside this API test."));
            builder.Services.AddSingleton<WorkspaceBackupService>();
            await using var app = builder.Build();
            app.UseExceptionHandler();
            app.MapWorkspaceEndpoints();
            app.MapFlowEndpoints();
            app.MapModuleEndpoints();
            await app.StartAsync();
            try
            {
                var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var http = new HttpClient { BaseAddress = new Uri(address) };
                var client = new MulseApiClient(http);
                const string password = "long-api-backup-password";
                var backup = await client.BackupWorkspaceAsync(password, CancellationToken.None);
                Assert.EndsWith(".mulsebackup", backup.FileName);
                Assert.Equal("application/octet-stream", backup.ContentType);
                var request = new Mulse.Ui.Models.RestoreWorkspaceRequestViewModel(Convert.ToBase64String(backup.Content), password, "merge");
                var preview = await client.PreviewWorkspaceRestoreAsync(request, CancellationToken.None);
                Assert.Equal(1, preview.FlowCount);
                Assert.Equal(2, preview.Conflicts.Count);
                Assert.Contains(preview.Warnings, warning => warning.Contains("original") && warning.Contains("Fetch: fetch"));
                Assert.True(store.GetState().Pipelines.Single().Enabled);
                using var badDelete = await http.PostAsJsonAsync("/api/workspace/flows/delete-all", new { Confirmation = "no" });
                Assert.Equal(HttpStatusCode.BadRequest, badDelete.StatusCode);
                using var badPassword = await http.PostAsJsonAsync("/api/workspace/restore/preview", request with { Password = "incorrect-password" });
                Assert.Equal(HttpStatusCode.BadRequest, badPassword.StatusCode);
                using var missingConfirmation = await http.PostAsJsonAsync("/api/workspace/restore", request with { Mode = "replace" });
                Assert.Equal(HttpStatusCode.BadRequest, missingConfirmation.StatusCode);
                Assert.Equal(1, await client.DeleteAllFlowsAsync("DELETE ALL FLOWS", CancellationToken.None));
                Assert.Empty(store.GetState().Pipelines);
                Assert.Equal("sensitive-value", store.GetState().ConfigValues["secret"]);
                await client.RestoreWorkspaceAsync(request with { Mode = "replace", Confirmation = "RESTORE WORKSPACE" }, CancellationToken.None);
                Assert.False(store.GetState().Pipelines.Single().Enabled);
                Assert.Equal("API tests", store.GetState().Pipelines.Single().Category);
                var restoredFlow = await client.GetFlowAsync("original", CancellationToken.None);
                Assert.Contains("Fetch: fetch", restoredFlow.MissingModules!);
                var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => client.SetFlowEnabledAsync("original", true, CancellationToken.None));
                Assert.Contains("unknown or unloaded", missing.Message);
                Assert.False(store.GetState().Pipelines.Single().Enabled);

                var sourceDirectory = Path.Combine(directory, "plugins");
                Directory.CreateDirectory(sourceDirectory);
                var assembly = Path.Combine(sourceDirectory, "fixture.dll");
                File.Copy(typeof(LifecycleTestInstaller).Assembly.Location, assembly);
                await catalog.SynchronizeAsync(CancellationToken.None);
                Assert.False((await client.GetModulePackagesAsync(CancellationToken.None)).Single(package => package.Id == "fixture").IsLoaded);
                Assert.DoesNotContain(await client.GetModulesAsync(CancellationToken.None), module => module.Id == "lifecycle-test-fetch");
                var loaded = await client.SetModulePackageEnabledAsync("fixture", true, CancellationToken.None);
                Assert.True(loaded.IsLoaded);
                using var noUnloadConfirmation = await http.PatchAsJsonAsync("/api/module-packages/fixture/enabled", new { Enabled = false });
                Assert.Equal(HttpStatusCode.BadRequest, noUnloadConfirmation.StatusCode);
                await store.UpsertFlowAsync(new PipelineDefinition
                {
                    Id = "uses-fixture", Enabled = false,
                    Fetches = [new FetchNodeDefinition { Id = "f", Fetch = new ModuleStepDefinition { Module = "lifecycle-test-fetch" } }]
                }, CancellationToken.None);
                using var inUse = await http.PatchAsJsonAsync("/api/module-packages/fixture/enabled", new { Enabled = false, Confirmation = "UNLOAD PACKAGE" });
                Assert.Equal(HttpStatusCode.Conflict, inUse.StatusCode);
                Assert.Contains("uses-fixture", await inUse.Content.ReadAsStringAsync());
                using var deleteInUse = await http.DeleteAsync("/api/module-packages/fixture");
                Assert.Equal(HttpStatusCode.Conflict, deleteInUse.StatusCode);
                Assert.True(store.GetState().ManagedPackages.Single().Enabled);
                await store.DeleteFlowAsync("uses-fixture", CancellationToken.None);
                var unloaded = await client.SetModulePackageEnabledAsync("fixture", false, CancellationToken.None);
                Assert.False(unloaded.IsLoaded);
                await catalog.SynchronizeAsync(CancellationToken.None);
                Assert.False(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
                Assert.True((await client.SetModulePackageEnabledAsync("fixture", true, CancellationToken.None)).IsLoaded);
                var original = store.GetState().Pipelines.Single();
                await store.UpsertFlowAsync(new PipelineDefinition
                {
                    Id = original.Id, Enabled = false,
                    Fetches = [new FetchNodeDefinition { Id = "f", Fetch = new ModuleStepDefinition { Module = "lifecycle-test-fetch" } }],
                    Parses = original.Parses, Deliveries = original.Deliveries
                }, CancellationToken.None);
                var pluginBackup = await client.BackupWorkspaceAsync(password, CancellationToken.None);
                var pluginRestore = new Mulse.Ui.Models.RestoreWorkspaceRequestViewModel(Convert.ToBase64String(pluginBackup.Content), password, "replace", "RESTORE WORKSPACE");
                var pluginPreview = await client.PreviewWorkspaceRestoreAsync(pluginRestore, CancellationToken.None);
                Assert.Contains(pluginPreview.Warnings, warning => warning.Contains("Fetch: lifecycle-test-fetch"));
                using (catalog.PinFlowModules(store.GetState().Pipelines.Single()))
                {
                    await client.RestoreWorkspaceAsync(pluginRestore, CancellationToken.None);
                    Assert.DoesNotContain(catalog.GetAll(), entry => entry.Descriptor.Id == "lifecycle-test-fetch");
                    await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.SynchronizeAsync(CancellationToken.None));
                    Assert.True(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
                }
                await catalog.SynchronizeAsync(CancellationToken.None);
                Assert.False(catalog.GetPackages().Single(package => package.Id == "fixture").IsLoaded);
                Assert.Contains("Fetch: lifecycle-test-fetch", (await client.GetFlowAsync("original", CancellationToken.None)).MissingModules!);
                await client.SetModulePackageEnabledAsync("fixture", true, CancellationToken.None);
                Assert.DoesNotContain("Fetch: lifecycle-test-fetch", (await client.GetFlowAsync("original", CancellationToken.None)).MissingModules!);
                await store.DeleteFlowAsync("original", CancellationToken.None);
                await client.SetModulePackageEnabledAsync("fixture", false, CancellationToken.None);
                using var builtin = await http.PatchAsJsonAsync("/api/module-packages/builtin.core/enabled", new { Enabled = false, Confirmation = "UNLOAD PACKAGE" });
                Assert.Equal(HttpStatusCode.Conflict, builtin.StatusCode);
            }
            finally { await app.StopAsync(); }
        }
        finally
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
