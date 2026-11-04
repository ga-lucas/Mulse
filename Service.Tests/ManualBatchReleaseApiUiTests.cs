using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class ManualBatchReleaseApiUiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UiConfirmationCallsRealEndpoint_AndDisplaysActualReleaseOrRetryOutcome(bool retry)
    {
        var store = new FakeRuntimeConfigurationStore(new()); var checkpoints = new FlowOrchestrationStateStore(store);
        var retries = new InMemoryFlowRetryStateStore(); var delivered = new List<IntegrationBatch>(); var fetches = 0;
        var payload = new IntegrationPayload("one.txt", BinaryData.FromString("one"), "text/plain", new Dictionary<string, string> { ["group"] = "A" });
        var catalog = new FakeExecutableModuleCatalog()
            .AddFetch(new DelegateFetchModule("source", (_, _, _, _) => { fetches++; return Task.FromResult(new IntegrationBatch([payload])); }))
            .AddParse(DelegateModuleFactory.PassthroughParse("parse"))
            .AddAugment(new DurableBatchAugmentModule(checkpoints, TimeProvider.System, NullLogger<DurableBatchAugmentModule>.Instance))
            .AddRender(DelegateModuleFactory.PassthroughRender("render"))
            .AddDeliver(DelegateModuleFactory.Deliver("destination", delivered, call => retry && call == 1));
        var pipeline = new PipelineDefinition
        {
            Id = "batches", Enabled = true, Retry = new() { MaxAttempts = 2, Delay = TimeSpan.Zero },
            Fetches = [new() { Id = "f", Fetch = new() { Module = "source" } }],
            Parses = [new() { Id = "p", FetchNodeIds = ["f"], Parse = new() { Module = "parse" } }],
            Augments = [new() { Module = "durable-batch-augment", Settings = new() { ["collectorId"] = "invoices", ["metadataKey"] = "group", ["targetCount"] = "10" } }],
            Deliveries = [new() { Render = new() { Module = "render" }, Delivers = [new() { Deliver = new() { Module = "destination" } }] }]
        };
        var definitions = new FlowDefinitionService(store, catalog); await definitions.CreateAsync(pipeline, default);
        var runtime = new FlowRuntime(definitions, catalog, checkpoints, retries, new ConfigValueResolver(store), TimeProvider.System, NullLogger<FlowRuntime>.Instance);
        await runtime.ExecuteAsync(pipeline.Id, default);
        var selected = Assert.Single(store.GetState().OrchestrationCheckpoints);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails(); builder.Services.AddExceptionHandler<ApiExceptionHandler>(); ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<IFlowDefinitionService>(definitions); builder.Services.AddSingleton<IModuleCatalog>(catalog);
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(store); builder.Services.AddSingleton<IFlowRuntime>(runtime);
        await using var app = builder.Build(); app.UseExceptionHandler(); app.MapFlowEndpoints(); await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var client = new MulseApiClient(http);
            using var unconfirmed = await http.PostAsJsonAsync($"/api/flows/batches/batches/{selected.InstanceId}/release", new { ConfirmInstanceId = "wrong", ExpectedPayloadCount = 1 });
            Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode); Assert.Empty(delivered);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReleaseCollectedBatchAsync("batches", selected.InstanceId, 2, default));
            Assert.False(Assert.Single(store.GetState().OrchestrationCheckpoints).Batch!.Ready);
            await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(client).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            async Task<string> Render(bool confirm, bool execute = false) => await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<ConfirmationHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["FlowId"] = "batches", ["AllowRelease"] = true, ["ConfirmForTest"] = confirm, ["ExecuteForTest"] = execute
                }))).ToHtmlString());
            Assert.Contains("Release collected batch...", await Render(false)); Assert.Empty(delivered);
            var confirmation = await Render(true); Assert.Contains("Confirm release", confirmation); Assert.Contains("1 collected payload(s)", confirmation); Assert.Empty(delivered);
            var result = await Render(true, true);
            Assert.Contains(retry ? "Batch release: Retrying" : "Batch release: Completed", result);
            Assert.Equal(1, fetches);
            if (retry)
            {
                Assert.True(Assert.Single(store.GetState().OrchestrationCheckpoints).Batch!.Ready);
                Assert.DoesNotContain("Release collected batch...", result);
                await Assert.ThrowsAsync<InvalidOperationException>(() => client.ReleaseCollectedBatchAsync("batches", selected.InstanceId, 1, default));
                Assert.Equal(FlowExecutionOutcome.Completed, (await runtime.ResumeRetryAsync(Assert.Single(retries.States), default))!.Outcome);
            }
            Assert.Single(delivered); Assert.Empty(store.GetState().OrchestrationCheckpoints); Assert.Equal(1, fetches);
        }
        finally { await app.StopAsync(); }
    }

    public sealed class ConfirmationHost : FlowCheckpoints
    {
        [Parameter] public bool ConfirmForTest { get; set; }
        [Parameter] public bool ExecuteForTest { get; set; }
        protected override async Task OnParametersSetAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            await (Task)typeof(FlowCheckpoints).GetMethod("RefreshAsync", flags)!.Invoke(this, null)!;
            if (!ConfirmForTest) return;
            var rows = (IReadOnlyList<FlowCheckpointViewModel>)typeof(FlowCheckpoints).GetField("_checkpoints", flags)!.GetValue(this)!;
            typeof(FlowCheckpoints).GetField("_confirm", flags)!.SetValue(this, Assert.Single(rows));
            if (ExecuteForTest) await (Task)typeof(FlowCheckpoints).GetMethod("ConfirmReleaseAsync", flags)!.Invoke(this, null)!;
        }
    }
}
