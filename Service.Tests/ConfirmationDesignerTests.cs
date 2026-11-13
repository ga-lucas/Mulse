using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class ConfirmationDesignerTests
{
    [Fact]
    public async Task Designer_LoadSaveAndReopen_PreservesConfirmation()
    {
        var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var designer = new Designer();
        typeof(Designer).GetProperty("ApiClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(designer, new MulseApiClient(http));
        typeof(Designer).GetProperty(nameof(Designer.FlowId))!.SetValue(designer, "flow");
        Invoke(designer, "BuildFromFlow", handler.Flow);
        Assert.True(Confirmation(designer));
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.True(handler.Saved!.Deliveries[0].Delivers[0].RequireConfirmation);
        Invoke(designer, "BuildFromFlow", handler.Flow);
        Assert.True(Confirmation(designer));
        var json = JsonSerializer.Serialize(handler.Saved, JsonSerializerOptions.Web);
        Assert.Contains("\"requireConfirmation\":true", json);
    }

    private static object? Invoke(Designer designer, string method, params object?[] args)
        => typeof(Designer).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(designer, args);
    private static bool Confirmation(Designer designer)
    {
        var nodes = (IEnumerable)typeof(Designer).GetField("_nodes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(designer)!;
        var deliver = nodes.Cast<object>().Single(n => (string)n.GetType().GetProperty("ModuleId")!.GetValue(n)! == "deliver");
        return (bool)deliver.GetType().GetProperty("RequireConfirmation")!.GetValue(deliver)!;
    }
    private sealed class Handler : HttpMessageHandler
    {
        private static FlowStepViewModel Step(string module) => new(module, new Dictionary<string, string>());
        public FlowViewModel Flow { get; private set; } = new("flow", false, "OnDemand", null, false,
            ["fetch"], ["parse"], ["fetch"], ["parse"], [], ["render"], ["deliver"],
            [new("fetch", Step("fetch"), [])], [new("parse", Step("parse"), ["fetch"])], [],
            [new(Step("render"), [new(Step("deliver"), RequireConfirmation: true)])]);
        public UpdateFlowRequestViewModel? Saved { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath == "/api/config-values/usages")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<ConfigReferenceUsageViewModel>()) };
            if (request.Method == HttpMethod.Put)
            {
                Saved = (await request.Content!.ReadFromJsonAsync<UpdateFlowRequestViewModel>(ct))!;
                Flow = Flow with { Deliveries = Saved.Deliveries.Select(route => new DeliveryRouteViewModel(
                    Step(route.Render.Module), route.Delivers.Select(action => new DeliveryActionViewModel(Step(action.Deliver.Module), RequireConfirmation: action.RequireConfirmation)).ToArray(), route.AtomicScope)).ToArray() };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Flow) };
        }
    }
}
