using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class DesignerSettingsUiTests
{
    private static FlowStepViewModel Step(string module, int attempts) => new(module,
        new Dictionary<string, string> { ["path"] = module + ".json" }, new(attempts, attempts, "Exponential", 120), module + " name");
    private static FlowViewModel Flow() => new("ui-retries", false, "OnDemand", null, false,
        ["fetch"], ["parsed"], ["fetch-module"], ["parse-module"], ["augment-module"], ["render-module"], ["deliver-module"],
        [new("fetch", Step("fetch-module", 2), [])], [new("parsed", Step("parse-module", 3), ["fetch"])],
        [Step("augment-module", 4)], [new(Step("render-module", 5), [new(Step("deliver-module", 6), Step("undo-module", 7))], "commit")],
        new(8, 8, "Linear", 240), "Finance", Name: "UI retry coverage");

    [Fact]
    public async Task LoadEditSaveReopen_PreservesEveryStepRetryAndCompensation()
    {
        var handler = new SavingHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
        var designer = new Designer();
        typeof(Designer).GetProperty("ApiClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(designer, new MulseApiClient(http));
        typeof(Designer).GetProperty(nameof(Designer.FlowId))!.SetValue(designer, handler.Current.Id);
        Invoke(designer, "BuildFromFlow", handler.Current);
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field(designer, "_errorMessage"));
        var saved = Assert.IsType<UpdateFlowRequestViewModel>(handler.Saved);
        var steps = Steps(saved).ToArray();
        Assert.Equal([2, 3, 4, 5, 6, 7], steps.Select(step => step.Retry!.MaxAttempts!.Value));
        Assert.Equal([2d, 3, 4, 5, 6, 7], steps.Select(step => step.Retry!.DelaySeconds));
        Assert.All(steps, step => { Assert.Equal("Exponential", step.Retry!.Backoff); Assert.Equal(120, step.Retry.MaxDelaySeconds); Assert.Equal(step.Module + " name", step.Name); Assert.Equal(step.Module + ".json", step.Settings["path"]); });
        Assert.Equal("Finance", saved.Category);
        Assert.Equal("commit", Assert.Single(saved.Deliveries).AtomicScope);
        Assert.Equal(8, saved.Retry!.MaxAttempts);

        Invoke(designer, "BuildFromFlow", handler.Current);
        var nodes = ((IEnumerable)Field(designer, "_nodes")!).Cast<object>().ToArray();
        var fetch = nodes.Single(node => Property<string>(node, "ModuleId") == "fetch-module");
        var deliver = nodes.Single(node => Property<string>(node, "ModuleId") == "deliver-module");
        fetch.GetType().GetProperty("Retry")!.SetValue(fetch, null);
        deliver.GetType().GetProperty("Retry")!.SetValue(deliver, new RetryPolicyRequestViewModel(1, 0, "Fixed", null));
        await (Task)Invoke(designer, "SaveAsync")!;
        Assert.Null(Field(designer, "_errorMessage"));
        Assert.Null(handler.Saved!.Fetches[0].Fetch.Retry);
        Assert.Equal(1, handler.Saved.Deliveries[0].Delivers[0].Deliver.Retry!.MaxAttempts);
        Assert.Equal(7, handler.Saved.Deliveries[0].Delivers[0].Compensation!.Retry!.MaxAttempts);
    }

    [Fact]
    public void CompensationChooser_UsesDeliveryDefaultsKeepsExistingSettingsAndSupportsRemoval()
    {
        var designer = new Designer();
        Invoke(designer, "BuildFromFlow", Flow());
        var deliver = ((IEnumerable)Field(designer, "_nodes")!).Cast<object>().Single(node => Property<string>(node, "ModuleId") == "deliver-module");
        var original = Property<FlowStepRequestViewModel>(deliver, "Compensation");
        Invoke(designer, "SetCompensationModule", deliver, "UNDO-MODULE");
        Assert.Same(original, Property<FlowStepRequestViewModel>(deliver, "Compensation"));
        typeof(Designer).GetField("_modules", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(designer, new ModuleViewModel[]
        {
            new("undo-new", "Undo delivery", "Deliver", "Undo action", [new("destination", "Destination", "Undo destination", true, "Text", "default-path", [])], [], [], []),
            new("not-deliver", "Not a delivery", "Fetch", "Fetch", [], [], [], [])
        });
        Invoke(designer, "SetCompensationModule", deliver, "not-deliver");
        Assert.Same(original, Property<FlowStepRequestViewModel>(deliver, "Compensation"));
        Assert.Contains("installed delivery", (string)Field(designer, "_errorMessage")!);
        Invoke(designer, "SetCompensationModule", deliver, "undo-new");
        var changed = Property<FlowStepRequestViewModel>(deliver, "Compensation");
        Assert.Equal("default-path", changed.Settings["destination"]);
        Assert.DoesNotContain("path", changed.Settings.Keys);
        Assert.Null(changed.Retry);
        Invoke(designer, "SetCompensationModule", deliver, "");
        Assert.Null(Property<FlowStepRequestViewModel?>(deliver, "Compensation"));
    }

    [Fact]
    public async Task RetryEditor_RendersOverrideFieldsAndPublishesEditsOrInheritance()
    {
        var policy = new RetryPolicyRequestViewModel(4, 2.5, "Exponential", 60);
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<RetryPolicyEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(RetryPolicyEditor.Policy)] = policy }));
            var html = rendered.ToHtmlString();
            Assert.Contains("Override flow retry policy", html);
            Assert.Contains("Max attempts", html);
            Assert.Contains("Max delay", html);
            Assert.Contains("value=\"2.5\"", html);
            Assert.Contains("Exponential", html);
        });
        var editor = new RetryPolicyEditor();
        RetryPolicyRequestViewModel? published = null;
        typeof(RetryPolicyEditor).GetProperty(nameof(RetryPolicyEditor.Policy))!.SetValue(editor, policy);
        typeof(RetryPolicyEditor).GetProperty(nameof(RetryPolicyEditor.PolicyChanged))!.SetValue(editor,
            EventCallback.Factory.Create<RetryPolicyRequestViewModel?>(new object(), value => published = value));
        typeof(RetryPolicyEditor).GetMethod("OnParametersSet", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null);
        await (Task)typeof(RetryPolicyEditor).GetMethod("PublishAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null)!;
        Assert.Equal(policy, published);
        typeof(RetryPolicyEditor).GetField("_enabled", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, false);
        await (Task)typeof(RetryPolicyEditor).GetMethod("PublishAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, null)!;
        Assert.Null(published);
    }

    private static IEnumerable<FlowStepRequestViewModel> Steps(UpdateFlowRequestViewModel flow)
        => flow.Fetches.Select(node => node.Fetch).Concat(flow.Parses.Select(node => node.Parse)).Concat(flow.Augments)
            .Concat(flow.Deliveries.SelectMany(route => new[] { route.Render }.Concat(route.Delivers.SelectMany(action => new[] { action.Deliver, action.Compensation! }))));
    private static T Property<T>(object instance, string name) => (T)instance.GetType().GetProperty(name)!.GetValue(instance)!;
    private static object? Field(Designer designer, string name) => typeof(Designer).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(designer);
    private static object? Invoke(Designer designer, string name, params object?[] args) => typeof(Designer).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(designer, args);

    private sealed class SavingHandler : HttpMessageHandler
    {
        public FlowViewModel Current { get; private set; } = Flow();
        public UpdateFlowRequestViewModel? Saved { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/config-values/usages")
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<ConfigReferenceUsageViewModel>()) };
            if (request.Method == HttpMethod.Put)
            {
                Saved = (await request.Content!.ReadFromJsonAsync<UpdateFlowRequestViewModel>(cancellationToken))!;
                Current = Current with
                {
                    Fetches = Saved.Fetches.Select(node => new FlowFetchNodeViewModel(node.Id, View(node.Fetch), node.InputParseIds)).ToArray(),
                    Parses = Saved.Parses.Select(node => new FlowParseNodeViewModel(node.Id, View(node.Parse), node.FetchNodeIds)).ToArray(),
                    Augments = Saved.Augments.Select(View).ToArray(),
                    Deliveries = Saved.Deliveries.Select(route => new DeliveryRouteViewModel(View(route.Render), route.Delivers.Select(action => new DeliveryActionViewModel(View(action.Deliver), action.Compensation is { } compensation ? View(compensation) : null)).ToArray(), route.AtomicScope)).ToArray()
                };
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(Current) };
        }
        private static FlowStepViewModel View(FlowStepRequestViewModel step)
            => new(step.Module, step.Settings, step.Retry is { } retry ? new(retry.MaxAttempts, retry.DelaySeconds, retry.Backoff, retry.MaxDelaySeconds) : null, step.Name);
    }
}
