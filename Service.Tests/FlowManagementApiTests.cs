using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules;
using Mulse.Ui.Services;
using Mulse.Ui.Models;
using Service;
using Service.Endpoints;
using Service.Middleware;

namespace Service.Tests;

public sealed class FlowManagementApiTests
{
    [Fact]
    public async Task UiClient_CategorizesDuplicatesAndDeletesThroughRealEndpoints()
    {
        var catalog = FakeModuleCatalog.WithModules(
            ("fetch", ModuleKind.Fetch), ("parse", ModuleKind.Parse),
            ("render", ModuleKind.Render), ("deliver", ModuleKind.Deliver));
        var store = new FakeRuntimeConfigurationStore(new RuntimeMulseState());
        var definitions = new FlowDefinitionService(store, catalog);
        await definitions.CreateAsync(new PipelineDefinition
        {
            Id = "original",
            Enabled = true,
            Fetches = [new FetchNodeDefinition { Id = "f", Fetch = new ModuleStepDefinition { Module = "fetch" } }],
            Parses = [new ParseNodeDefinition { Id = "p", FetchNodeIds = ["f"], Parse = new ModuleStepDefinition { Module = "parse" } }],
            Deliveries = [new DeliveryRouteDefinition
            {
                Render = new ModuleStepDefinition { Module = "render" },
                Delivers = [new DeliveryActionDefinition { Deliver = new ModuleStepDefinition { Module = "deliver" } }]
            }]
        }, CancellationToken.None);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        ApiTestValidation.Register(builder.Services);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton<IFlowDefinitionService>(definitions);
        builder.Services.AddSingleton<IModuleCatalog>(catalog);
        builder.Services.AddSingleton<IRuntimeConfigurationStore>(store);
        builder.Services.AddSingleton<IFlowRuntime>(_ => throw new NotSupportedException("Flow execution is outside this test."));
        await using var app = builder.Build();
        app.UseExceptionHandler();
        app.MapFlowEndpoints();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var http = new HttpClient { BaseAddress = new Uri(address) };
            var client = new MulseApiClient(http);
            var updated = await client.SetFlowCategoryAsync("original", "  Finance  ", CancellationToken.None);
            Assert.Equal("Finance", updated.Category);
            Assert.True(updated.Enabled);
            var copy = await client.DuplicateFlowAsync("original", "copy", CancellationToken.None);
            Assert.Equal("copy", copy.Id);
            Assert.False(copy.Enabled);
            Assert.Equal("Finance", copy.Category);
            Assert.Equal(2, (await client.GetFlowsAsync(CancellationToken.None)).Count);
            var copyRequest = System.Text.Json.JsonSerializer.Deserialize<UpdateFlowRequestViewModel>(System.Text.Json.JsonSerializer.Serialize(copy))!;
            copyRequest = copyRequest with { Trigger = new FlowTriggerRequestViewModel(copy.TriggerMode, copy.Interval, copy.RunOnStartup) };
            var renamed = await client.UpdateFlowAsync(copyRequest with { Name = "  Order import  " }, CancellationToken.None);
            Assert.Equal("copy", renamed.Id);
            Assert.Equal("Order import", renamed.Name);
            Assert.Equal("Order import", (await client.GetFlowAsync("copy", CancellationToken.None)).Name);
            var enabled = await client.SetFlowEnabledAsync("copy", true, CancellationToken.None);
            Assert.Equal("Order import", enabled.Name);
            Assert.Equal("Finance", enabled.Category);
            var cleared = await client.SetFlowCategoryAsync("copy", null, CancellationToken.None);
            Assert.Null(cleared.Category);
            Assert.Equal("Order import", cleared.Name);
            var namedRequest = new CreateFlowRequestViewModel("named", false, copyRequest.Trigger, copyRequest.Fetches,
                copyRequest.Parses, copyRequest.Augments, copyRequest.Deliveries, Name: "Named creation");
            var named = await client.CreateFlowAsync(namedRequest, CancellationToken.None);
            Assert.Equal("Named creation", named.Name);
            await client.DeleteFlowAsync("named", CancellationToken.None);

            using var conflict = await http.PostAsJsonAsync("/api/flows/original/duplicate", new { Id = "COPY" });
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var invalid = await http.PostAsJsonAsync("/api/flows/original/duplicate", new { Id = " " });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            using var missing = await http.PostAsJsonAsync("/api/flows/missing/duplicate", new { Id = "new" });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.DuplicateFlowAsync("original", "copy", CancellationToken.None));
            Assert.Contains("already exists", error.Message);

            await client.DeleteFlowAsync("copy", CancellationToken.None);
            Assert.Equal("original", (await client.GetFlowsAsync(CancellationToken.None)).Single().Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.DeleteFlowAsync("missing", CancellationToken.None));
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
