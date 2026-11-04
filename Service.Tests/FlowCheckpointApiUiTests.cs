using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class FlowCheckpointApiUiTests
{
    [Fact]
    public async Task InventoryAndUiShowOnlyScopedCountsAndStatus_AndSurfaceErrors()
    {
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState
        {
            Pipelines = [new() { Id = "batches" }, new() { Id = "empty" }],
            OrchestrationCheckpoints = [new()
            {
                FlowId = "batches", InstanceId = "instance-A", CorrelationKey = "A", Bookmark = "Invoices",
                Batch = new() { TargetCount = 3 },
                Payloads = [new() { Name = "PRIVATE-FILE-NAME", ContentBase64 = Convert.ToBase64String("PRIVATE-BODY"u8.ToArray()) }],
                SourceReceipts = [new("PRIVATE-RECEIPT", "f", new() { Module = "fetch", Settings = new() { ["password"] = "PRIVATE-SECRET" } }, new() { ["source"] = "PRIVATE-SOURCE" })]
            }, new() { FlowId = "different", InstanceId = "OTHER-INSTANCE", CorrelationKey = "B" }]
        });
        var catalog = FakeModuleCatalog.WithModules();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<IFlowDefinitionService>(new FlowDefinitionService(store, catalog));
        builder.Services.AddSingleton<IModuleCatalog>(catalog);
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(store);
        builder.Services.AddSingleton<IFlowRuntime>(_ => throw new NotSupportedException("Inspection must not execute a flow."));
        await using var app = builder.Build();
        app.UseExceptionHandler(); app.MapFlowEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            using var response = await http.GetAsync("/api/flows/BATCHES/checkpoints");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain("PRIVATE", json); Assert.DoesNotContain("OTHER-INSTANCE", json);
            var checkpoint = Assert.Single(await client.GetFlowCheckpointsAsync("batches", default));
            Assert.Equal(1, checkpoint.PayloadCount); Assert.Equal(1, checkpoint.PendingSourceCount);
            Assert.Equal(3, checkpoint.TargetCount); Assert.False(checkpoint.BatchReady);
            Assert.Empty(await client.GetFlowCheckpointsAsync("empty", default));
            var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetFlowCheckpointsAsync("missing", default));
            Assert.Contains("No configured flow", missing.Message);
            await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(client).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            async Task<string> Render(string flow) => await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<LoadedCheckpoints>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["FlowId"] = flow }))).ToHtmlString());
            var html = await Render("batches");
            Assert.Contains("Invoices", html); Assert.Contains("Collecting", html); Assert.Contains("Minimum count: 3", html);
            Assert.Contains("1 payload(s)", html); Assert.DoesNotContain("PRIVATE", html);
            Assert.Contains("No pending checkpoints", await Render("empty"));
            Assert.Contains("Unable to read checkpoints", await Render("missing"));
            var ready = store.GetState().OrchestrationCheckpoints[0];
            await store.UpsertOrchestrationCheckpointAsync(new()
            {
                FlowId = ready.FlowId, CorrelationKey = ready.CorrelationKey, InstanceId = ready.InstanceId, Bookmark = ready.Bookmark,
                Payloads = ready.Payloads, SourceReceipts = ready.SourceReceipts, Batch = new() { TargetCount = 3, Ready = true }
            }, default);
            var released = await Render("batches");
            Assert.Contains("awaiting completion or recovery", released); Assert.Contains("New runs are blocked", released);
        }
        finally { await app.StopAsync(); }
    }

    public sealed class LoadedCheckpoints : FlowCheckpoints
    {
        protected override Task OnParametersSetAsync() => (Task)typeof(FlowCheckpoints)
            .GetMethod("RefreshAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(this, null)!;
    }
}
