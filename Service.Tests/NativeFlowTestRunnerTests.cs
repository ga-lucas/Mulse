using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.ControlFlow;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;
using Service.Endpoints;
using Service.Execution;
using Service.Middleware;
using Service.Models;

namespace Service.Tests;

public sealed class NativeFlowTestRunnerTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
    private static NativeFlowTestPayload Payload(string text) => new("sample.xml", text, "application/xml", new() { ["group"] = "test", ["messageId"] = "1", ["channel"] = "input" });
    private static NativeFlowTestRequest Request(params NativeFlowNode[] nodes) => new(
        JsonSerializer.Serialize(new NativeFlowDefinition { Durable = true, Nodes = nodes.ToList() }, Json), Payload("<source/>"), []);

    [Fact]
    public async Task IsolatedRunCapturesExplicitMocksAndNamedDeliveriesWithoutResolvingModules()
    {
        var request = Request(new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "source" },
            new() { Kind = NativeFlowNodeKind.Augment, Module = "unavailable-augment", InputMessages = ["source"], MessageName = "output" },
            new() { Kind = NativeFlowNodeKind.Deliver, Module = "unavailable-destination", MessageName = "output" },
            new() { Kind = NativeFlowNodeKind.Reply, MessageName = "source" });
        request.Mocks.Add(new("Augment", "unavailable-augment", Payload("<mapped/>")));
        request.Mocks.Add(new("Deliver", "unavailable-destination", null));
        var result = await NativeFlowTestRunner.RunAsync(request, default);
        Assert.Equal("Continue", result.Status);
        Assert.Equal(new[] { "Augment", "Deliver" }, result.Calls.Select(call => call.Kind));
        Assert.Equal("<source/>", Assert.Single(result.Calls[0].Input).Content);
        Assert.Equal("<mapped/>", Assert.Single(result.Calls[1].Input).Content);
        Assert.Equal("<source/>", Assert.Single(result.Output).Content);
        Assert.Equal("<source/>", Assert.Single(result.Replies).Content);
    }

    [Fact]
    public async Task DeliverCallsRecordEffectiveSettingsWithComputedValuesAndRedactedSecrets()
    {
        var request = Request(
            new() { Kind = NativeFlowNodeKind.DeclareVariable, Variable = "host", VariableType = NativeScalarType.String, Expression = new() { Literal = NativeScalar.From("sftp.example.test") } },
            new() { Kind = NativeFlowNodeKind.Deliver, Module = "sftp-sink", Settings = new() { ["password"] = "hunter2", ["remotePath"] = "/out" },
                SettingExpressions = new() { ["host"] = new() { Kind = NativeExpressionKind.Variable, Variable = "host" } } });
        request.Mocks.Add(new("Deliver", "sftp-sink", null));
        var result = await NativeFlowTestRunner.RunAsync(request, default);
        var settings = Assert.Single(result.Calls).Settings!;
        Assert.Equal("sftp.example.test", settings["host"]);
        Assert.Equal("/out", settings["remotePath"]);
        Assert.Equal("***", settings["password"]);
    }

    [Fact]
    public async Task MockOutputMetadataSelectsTheNativeDecisionBranch()
    {
        var request = Request(new() { Kind = NativeFlowNodeKind.Augment, Module = "policy" },
            new() { Kind = NativeFlowNodeKind.If, Conditions = [new() { Source = Mulse.Modules.Decisions.DecisionValueSourceKind.Metadata,
                Path = "route", Operator = Mulse.Modules.Decisions.DecisionComparisonOperator.Equals, Value = "billing" }],
                Children = [new() { Kind = NativeFlowNodeKind.Deliver, Module = "billing-sink" }],
                Otherwise = [new() { Kind = NativeFlowNodeKind.Deliver, Module = "other-sink" }] });
        var output = Payload("<after-policy/>"); output.Metadata["route"] = "billing";
        request.Mocks.Add(new("Augment", "policy", output)); request.Mocks.Add(new("Deliver", "billing-sink", null));
        var result = await NativeFlowTestRunner.RunAsync(request, default);
        Assert.Equal(new[] { "policy", "billing-sink" }, result.Calls.Select(call => call.Module));
        Assert.Equal("billing", Assert.Single(result.Calls[1].Input).Metadata["route"]);
    }

    [Fact]
    public async Task MissingMockAndBlockedDefinitionFailExplicitly()
    {
        var missing = Request(new NativeFlowNode { Kind = NativeFlowNodeKind.Render, Module = "not-loaded" });
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeFlowTestRunner.RunAsync(missing, default));
        Assert.Contains("No explicit Render mock", exception.Message);
        var blocked = Request(new NativeFlowNode { Kind = NativeFlowNodeKind.Blocked, BlockReason = "untranslated" });
        await Assert.ThrowsAsync<NotSupportedException>(() => NativeFlowTestRunner.RunAsync(blocked, default));
    }

    [Fact]
    public async Task WaitReturnsSuspendedWithoutPretendingToDeliver()
    {
        var request = Request(new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "source" },
            new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 10 },
            new() { Kind = NativeFlowNodeKind.Deliver, Module = "sink", MessageName = "source" });
        request.Mocks.Add(new("Deliver", "sink", null));
        var result = await NativeFlowTestRunner.RunAsync(request, default);
        Assert.Equal("Suspended", result.Status); Assert.Contains("Native wait", result.SuspensionReason);
        Assert.Empty(result.Calls);
    }

    [Fact]
    public async Task AdvanceWaitsResumesTimerWaitOnVirtualClockAndDeliversOnce()
    {
        var request = Request(new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "source" },
            new() { Kind = NativeFlowNodeKind.Wait, DurationSeconds = 3600 },
            new() { Kind = NativeFlowNodeKind.Deliver, Module = "sink", MessageName = "source" }) with { AdvanceWaits = true };
        request.Mocks.Add(new("Deliver", "sink", null));
        var started = DateTimeOffset.UtcNow;
        var result = await NativeFlowTestRunner.RunAsync(request, default);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromMinutes(1));
        Assert.Equal("Continue", result.Status); Assert.Equal(1, result.WaitResumes);
        Assert.Equal("sink", Assert.Single(result.Calls).Module);
    }

    [Fact]
    public async Task DuplicateMocksAndOversizedInputAreRejected()
    {
        var request = Request(new NativeFlowNode { Kind = NativeFlowNodeKind.Augment, Module = "module" });
        request.Mocks.Add(new("Augment", "module", Payload("<a/>"))); request.Mocks.Add(new("Augment", "module", Payload("<b/>")));
        await Assert.ThrowsAsync<ArgumentException>(() => NativeFlowTestRunner.RunAsync(request, default));
        await Assert.ThrowsAsync<ArgumentException>(() => NativeFlowTestRunner.RunAsync(request with { Input = Payload(new string('x', 1_000_001)), Mocks = [] }, default));
    }

    [Fact]
    public async Task HttpEndpointReturnsMockTraceWithoutRealCatalogOrWorkspace()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
        builder.Services.AddProblemDetails(); builder.Services.AddExceptionHandler<ApiExceptionHandler>(); ApiTestValidation.Register(builder.Services);
        builder.Services.AddSingleton<IFlowDesignService>(_ => throw new InvalidOperationException("Isolated native test must not use flow design services."));
        await using var app = builder.Build(); app.UseExceptionHandler(); app.MapDesignerEndpoints(); await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()) };
            var request = Request(new() { Kind = NativeFlowNodeKind.Receive, Channel = "input", MessageName = "source" },
                new() { Kind = NativeFlowNodeKind.Deliver, Module = "no-real-module", MessageName = "source" });
            request.Mocks.Add(new("Deliver", "no-real-module", null));
            using var response = await client.PostAsJsonAsync("/api/flow-designer/native/test", request);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var result = await response.Content.ReadFromJsonAsync<NativeFlowTestResponse>();
            Assert.Equal("no-real-module", Assert.Single(result!.Calls).Module);
            Assert.Equal("<source/>", Assert.Single(Assert.Single(result.Calls).Input).Content);
            await using var services = new ServiceCollection().AddLogging().AddSingleton<IMulseApiClient>(new MulseApiClient(client)).BuildServiceProvider();
            await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
            var nodes = JsonSerializer.Deserialize<NativeFlowEditorModel>(request.FlowJson, JsonSerializerOptions.Web)!.Nodes;
            var html = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TestEditorHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { ["Value"] = request.FlowJson, ["Nodes"] = nodes }))).ToHtmlString());
            Assert.Contains("Test status: Continue", html); Assert.Contains("no-real-module", html); Assert.Contains("&lt;ui-sample/&gt;", html);
            Assert.DoesNotContain("status-banner error", html);
            var route = Request(new() { Kind = NativeFlowNodeKind.Augment, Module = "policy" },
                new() { Kind = NativeFlowNodeKind.If, Conditions = [new() { Source = Mulse.Modules.Decisions.DecisionValueSourceKind.Metadata,
                    Path = "route", Operator = Mulse.Modules.Decisions.DecisionComparisonOperator.Equals, Value = "billing" }],
                    Children = [new() { Kind = NativeFlowNodeKind.Deliver, Module = "billing-sink" }] });
            var routeNodes = JsonSerializer.Deserialize<NativeFlowEditorModel>(route.FlowJson, JsonSerializerOptions.Web)!.Nodes;
            var routeHtml = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<TestMetadataEditorHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
                { ["Value"] = route.FlowJson, ["Nodes"] = routeNodes }))).ToHtmlString());
            Assert.Contains("Test status: Continue", routeHtml); Assert.Contains("route: billing", routeHtml); Assert.Contains("Deliver: billing-sink", routeHtml);
            Assert.DoesNotContain("status-banner error", routeHtml);
        }
        finally { await app.StopAsync(); }
    }

    public sealed class TestMetadataEditorHost : NativeFlowTestEditor
    {
        protected override async Task OnParametersSetAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var mocks = ((System.Collections.IEnumerable)typeof(NativeFlowTestEditor).GetField("_mocks", flags)!.GetValue(this)!).Cast<object>().ToArray();
            foreach (var item in mocks) item.GetType().GetProperty("Enabled")!.SetValue(item, true);
            var policy = mocks.Single(item => (string)item.GetType().GetProperty("Module")!.GetValue(item)! == "policy");
            policy.GetType().GetProperty("UseCustomMetadata")!.SetValue(policy, true);
            typeof(NativeFlowTestEditor).GetMethod("AddOutputMetadata", flags)!.Invoke(this, [policy]);
            var entry = ((System.Collections.IEnumerable)policy.GetType().GetProperty("Metadata")!.GetValue(policy)!).Cast<object>().Single();
            entry.GetType().GetProperty("Key")!.SetValue(entry, "route"); entry.GetType().GetProperty("Value")!.SetValue(entry, "billing");
            await (Task)typeof(NativeFlowTestEditor).GetMethod("RunAsync", flags)!.Invoke(this, null)!;
        }
    }

    public sealed class TestEditorHost : NativeFlowTestEditor
    {
        protected override async Task OnParametersSetAsync()
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            typeof(NativeFlowTestEditor).GetField("_content", flags)!.SetValue(this, "<ui-sample/>");
            var mocks = (System.Collections.IEnumerable)typeof(NativeFlowTestEditor).GetField("_mocks", flags)!.GetValue(this)!;
            var mock = mocks.Cast<object>().Single();
            mock.GetType().GetProperty("Enabled")!.SetValue(mock, true);
            await (Task)typeof(NativeFlowTestEditor).GetMethod("RunAsync", flags)!.Invoke(this, null)!;
        }
    }
}
