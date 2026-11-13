using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

public sealed class HistoryRenderingTests
{
    private static readonly Guid InboxId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ExecutionMessageId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private const string Secret = "sensitive-payload-marker";
    private static readonly Guid UnknownDeliveryId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid CompletedDeliveryId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static string ConfirmationPath => $"/api/history/executions/run-new/deliveries/{UnknownDeliveryId:D}/confirm-delivered";

    [Fact]
    public async Task History_ConfirmationRequiresUnknownDeliveryEvidenceAndExactTypedConfirmation()
    {
        using var handler = new HistoryHandler();
        await RenderAsync(handler, async (page, html) =>
        {
            await InvokeAsync(page, "ConfirmDeliveryAsync");
            await InvokeAsync(page, "SelectExecutionAsync", "run-new");
            Assert.Equal(1, html().Split(">Confirm independently verified delivery</button>").Length - 1);
            await InvokeAsync(page, "BeginDeliveryConfirmation", CompletedDeliveryId);
            Assert.DoesNotContain("delivery-evidence", html());
            await InvokeAsync(page, "ConfirmDeliveryAsync");
            await InvokeAsync(page, "BeginDeliveryConfirmation", UnknownDeliveryId);
            Assert.Contains("does NOT resend the effect", html());
            Assert.Contains("source acknowledgements", html());
            Assert.Contains("response data would be missing", html());
            Assert.Matches("<button[^>]*disabled[^>]*>Confirm verified success", html());
            await InvokeAsync(page, "ConfirmDeliveryAsync");

            SetField(page, "_deliveryEvidence", "Verified receipt #123 at destination");
            await InvokeAsync(page, "ConfirmDeliveryAsync");
            foreach (var invalid in new[] { "confirm delivered", "CONFIRM DELIVERED ", "CONFIRM" })
            {
                SetField(page, "_deliveryConfirmation", invalid);
                await InvokeAsync(page, "ConfirmDeliveryAsync");
            }
            SetField(page, "_deliveryConfirmation", "CONFIRM DELIVERED");
            SetField(page, "_deliveryEvidence", "  ");
            await InvokeAsync(page, "ConfirmDeliveryAsync");
            Assert.Empty(handler.Posts);

            SetField(page, "_deliveryEvidence", "Verified receipt #123 at destination");
            await page.SetParametersAsync(ParameterView.Empty);
            Assert.DoesNotMatch("<button[^>]*disabled[^>]*>Confirm verified success", html());
            await InvokeAsync(page, "ConfirmDeliveryAsync");
            var post = Assert.Single(handler.Posts);
            Assert.Equal(ConfirmationPath, post.Path);
            using var body = System.Text.Json.JsonDocument.Parse(post.Body);
            Assert.Equal("CONFIRM DELIVERED", body.RootElement.GetProperty("confirmation").GetString());
            Assert.Equal("Verified receipt #123 at destination", body.RootElement.GetProperty("evidence").GetString());
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            Assert.Equal(2, handler.Requests.Count(path => path == "/api/history/executions/run-new"));
            Assert.Contains("Delivery confirmed as independently verified", html());
            Assert.Contains("Confirmed", html());
            Assert.DoesNotContain("delivery-evidence", html());
            Assert.DoesNotContain(">Confirm independently verified delivery</button>", html());
        });
    }

    [Fact]
    public async Task History_ConfirmationDisplaysBackendProblemWithoutSuccessOrRefresh()
    {
        using var handler = new HistoryHandler(ConfirmationPath);
        await RenderAsync(handler, async (page, html) =>
        {
            await InvokeAsync(page, "SelectExecutionAsync", "run-new");
            await InvokeAsync(page, "BeginDeliveryConfirmation", UnknownDeliveryId);
            SetField(page, "_deliveryEvidence", "Verified receipt #123");
            SetField(page, "_deliveryConfirmation", "CONFIRM DELIVERED");
            await InvokeAsync(page, "ConfirmDeliveryAsync");
            Assert.Single(handler.Posts);
            Assert.Contains("Unable to confirm delivery", html());
            Assert.Contains("Request-response deliveries cannot use this confirmation because response data would be missing", html());
            Assert.DoesNotContain("Delivery confirmed as independently verified", html());
            Assert.Contains("delivery-evidence", html());
            Assert.Equal(1, handler.Requests.Count(path => path == "/api/history/executions/run-new"));
        });
    }

    [Fact]
    public async Task History_ClosureRequiresHeldStateSeparateEvidenceAndExactConfirmation()
    {
        using var handler = new HistoryHandler();
        await RenderAsync(handler, async (page, html) =>
        {
            await InvokeAsync(page, "CloseExecutionAsync");
            await InvokeAsync(page, "SelectExecutionAsync", "run-new");
            Assert.DoesNotContain("Close held execution without replay", html());
            await InvokeAsync(page, "BeginExecutionClosure");
            Assert.DoesNotContain("close-evidence", html());
            SetField(page, "_closeConfirmation", "CLOSE WITHOUT REPLAY");
            SetField(page, "_closeEvidence", "Independent review");
            await InvokeAsync(page, "CloseExecutionAsync");
            Assert.Empty(handler.Posts);

            await InvokeAsync(page, "SelectExecutionAsync", "run-held");
            Assert.Contains("Close held execution without replay", html());
            await InvokeAsync(page, "BeginExecutionClosure");
            Assert.Contains("abandons the held input", html());
            Assert.Contains("never resends or rolls back effects", html());
            Assert.Contains("may lose unprocessed work", html());
            Assert.Contains("Independent review is required", html());
            Assert.Contains("resolve existing recovery first", html());
            Assert.Matches("<button[^>]*disabled[^>]*>Confirm close without replay", html());
            SetField(page, "_deliveryEvidence", "Other action evidence must not count");
            SetField(page, "_deliveryConfirmation", "CONFIRM DELIVERED");
            await InvokeAsync(page, "CloseExecutionAsync");
            SetField(page, "_closeEvidence", "Reviewed destination and accepted abandoned work");
            foreach (var invalid in new[] { "", "close without replay", "CLOSE WITHOUT REPLAY " })
            {
                SetField(page, "_closeConfirmation", invalid);
                await InvokeAsync(page, "CloseExecutionAsync");
            }
            SetField(page, "_closeConfirmation", "CLOSE WITHOUT REPLAY");
            SetField(page, "_closeEvidence", "  ");
            await InvokeAsync(page, "CloseExecutionAsync");
            Assert.Empty(handler.Posts);

            SetField(page, "_closeEvidence", "Reviewed destination and accepted abandoned work");
            await InvokeAsync(page, "CloseExecutionAsync");
            var post = Assert.Single(handler.Posts);
            Assert.Equal("/api/history/executions/run-held/close", post.Path);
            using var body = System.Text.Json.JsonDocument.Parse(post.Body);
            Assert.Equal("CLOSE WITHOUT REPLAY", body.RootElement.GetProperty("confirmation").GetString());
            Assert.Equal("Reviewed destination and accepted abandoned work", body.RootElement.GetProperty("evidence").GetString());
            Assert.Equal(2, body.RootElement.EnumerateObject().Count());
            Assert.Equal(2, handler.Requests.Count(path => path == "/api/history/executions/run-held"));
            Assert.Contains("Held execution closed without replay", html());
            Assert.Contains("Closed", html());
            Assert.DoesNotContain("close-evidence", html());
            Assert.DoesNotContain("Close held execution without replay", html());
        });
    }

    [Fact]
    public async Task History_ClosureShowsRecoveryConflictWithoutSuccess()
    {
        using var handler = new HistoryHandler("/api/history/executions/run-held/close");
        await RenderAsync(handler, async (page, html) =>
        {
            await InvokeAsync(page, "SelectExecutionAsync", "run-held");
            await InvokeAsync(page, "BeginExecutionClosure");
            SetField(page, "_closeEvidence", "Independent review completed");
            SetField(page, "_closeConfirmation", "CLOSE WITHOUT REPLAY");
            await InvokeAsync(page, "CloseExecutionAsync");
            Assert.Single(handler.Posts);
            Assert.Contains("Unable to close held execution", html());
            Assert.Contains("Resolve existing retry or checkpoint recovery first", html());
            Assert.DoesNotContain("Held execution closed without replay", html());
            Assert.Contains("close-evidence", html());
            Assert.Equal(1, handler.Requests.Count(path => path == "/api/history/executions/run-held"));
        });
    }

    private static void SetField(History page, string name, string value)
        => typeof(History).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    [Fact]
    public async Task History_ShowsInboxWithoutExecutionAndSelectedExecutionStatuses()
    {
        using var handler = new HistoryHandler();
        await RenderAsync(handler, async (page, html) =>
        {
            var initial = html();
            Assert.Contains("Inbox / messages", initial);
            Assert.Contains(InboxId.ToString(), initial);
            Assert.Contains("Not assigned", initial);
            Assert.Contains("Pending", initial);
            Assert.Contains("Claimed", initial);
            Assert.Contains("Held", initial);
            Assert.Contains("orders-flow", initial);
            Assert.Contains("Completed", initial);
            Assert.True(initial.IndexOf("run-new", StringComparison.Ordinal) < initial.IndexOf("run-old", StringComparison.Ordinal));
            Assert.Contains("/api/history/executions?take=50", handler.Requests);
            Assert.Contains("/api/history/messages?take=50", handler.Requests);
            Assert.DoesNotContain("Execution detail", initial);
            Assert.Contains("sensitive data", initial);
            Assert.Contains("not automatically replayable", initial);
            Assert.Contains("legacy Json", initial);

            await InvokeAsync(page, "SelectExecutionAsync", "run-new");
            var detail = html();
            Assert.Contains("Execution detail", detail);
            Assert.Contains("Events", detail);
            Assert.Contains("parse-stage", detail);
            Assert.Contains("Succeeded", detail);
            Assert.Contains("Parsed 2 records", detail);
            Assert.Contains("Deliveries", detail);
            Assert.Contains("delivery-stage", detail);
            Assert.Contains("Unknown", detail);
            Assert.Contains("execution-channel", detail);
            Assert.Contains("2026-11-13", detail);
            Assert.DoesNotContain(Secret, detail);
            Assert.DoesNotContain(handler.Requests, path => path.StartsWith("/api/history/messages/", StringComparison.Ordinal));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task History_OnlyFetchesAndShowsPayloadAfterExplicitInspection(bool fromExecution)
    {
        using var handler = new HistoryHandler();
        await RenderAsync(handler, async (page, html) =>
        {
            var messageId = fromExecution ? ExecutionMessageId : InboxId;
            if (fromExecution)
                await InvokeAsync(page, "SelectExecutionAsync", "run-new");

            Assert.Contains("Inspect payload", html());
            Assert.DoesNotContain("<pre>", html());
            Assert.DoesNotContain(Secret, html());
            Assert.DoesNotContain(handler.Requests, path => path.StartsWith("/api/history/messages/", StringComparison.Ordinal));

            await InvokeAsync(page, "InspectPayloadAsync", messageId);
            Assert.Contains(Secret, html());
            Assert.Contains("&lt;script&gt;", html());
            Assert.DoesNotContain("<script>", html());
            Assert.Equal(1, handler.Requests.Count(path => path == $"/api/history/messages/{messageId:D}"));

            await InvokeAsync(page, "HidePayload");
            Assert.DoesNotContain(Secret, html());
            Assert.DoesNotContain("<pre>", html());
            Assert.Equal(1, handler.Requests.Count(path => path.StartsWith("/api/history/messages/", StringComparison.Ordinal)));
        });
    }

    [Theory]
    [InlineData("/api/history/executions", "Unable to load execution history")]
    [InlineData("/api/history/messages", "Unable to load inbox history")]
    [InlineData("/api/history/executions/run-new", "Unable to load execution details")]
    [InlineData("/api/history/messages/11111111-1111-1111-1111-111111111111", "Unable to inspect message payload")]
    public async Task History_DisplaysProblemDetailsForEachFailedRequest(string failedPath, string expectedError)
    {
        using var handler = new HistoryHandler(failedPath);
        await RenderAsync(handler, async (page, html) =>
        {
            if (failedPath == "/api/history/executions/run-new")
                await InvokeAsync(page, "SelectExecutionAsync", "run-new");
            else if (failedPath.EndsWith(InboxId.ToString(), StringComparison.Ordinal))
                await InvokeAsync(page, "InspectPayloadAsync", InboxId);

            Assert.Contains(expectedError, html());
            Assert.Contains("History unavailable in legacy Json mode", html());
            Assert.Contains("role=\"alert\"", html());
            Assert.DoesNotContain(Secret, html());
            if (failedPath == "/api/history/executions")
                Assert.Contains(InboxId.ToString(), html());
            if (failedPath == "/api/history/messages")
                Assert.Contains("orders-flow", html());
        });
    }

    [Fact]
    public async Task History_RefreshClearsPayloadAndFiltersExecutionsOnly()
    {
        using var handler = new HistoryHandler();
        await RenderAsync(handler, async (page, html) =>
        {
            await InvokeAsync(page, "InspectPayloadAsync", InboxId);
            Assert.Contains(Secret, html());
            typeof(History).GetField("_flowId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, " orders & billing ");
            await InvokeAsync(page, "LoadAsync");
            Assert.Contains("/api/history/executions?take=50&flowId=orders%20%26%20billing", handler.Requests);
            Assert.Equal(2, handler.Requests.Count(path => path == "/api/history/messages?take=50"));
            Assert.DoesNotContain(Secret, html());
        });
    }

    private static async Task RenderAsync(HistoryHandler handler, Func<History, Func<string>, Task> test)
    {
        using var httpClient = new HttpClient(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost") };
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IMulseApiClient>(new MulseApiClient(httpClient)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            History? page = null;
            var rendered = await renderer.RenderComponentAsync<HistoryHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(HistoryHost.Capture)] = (Action<History>)(component => page = component)
            }));
            Assert.NotNull(page);
            await test(page, rendered.ToHtmlString);
        });
    }

    private static async Task InvokeAsync(History page, string method, params object[] arguments)
    {
        var result = typeof(History).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, arguments);
        if (result is Task task)
            await task;
        await page.SetParametersAsync(ParameterView.Empty);
    }

    private sealed class HistoryHost : ComponentBase
    {
        [Parameter]
        public Action<History>? Capture { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<History>(0);
            builder.AddComponentReferenceCapture(1, component => Capture?.Invoke((History)component));
            builder.CloseComponent();
        }
    }

    private sealed class HistoryHandler(string? failedPath = null) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<(string Path, string Body)> Posts { get; } = [];
        private bool _confirmed;
        private bool _closed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Post)
            {
                Assert.Contains(path, new[] { ConfirmationPath, "/api/history/executions/run-held/close" });
                Posts.Add((path, await request.Content!.ReadAsStringAsync(cancellationToken)));
            }
            else
            {
                Assert.Equal(HttpMethod.Get, request.Method);
            }
            if (path == failedPath)
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = JsonContent.Create(new Microsoft.AspNetCore.Mvc.ProblemDetails
                    {
                        Detail = path == ConfirmationPath
                            ? "Request-response deliveries cannot use this confirmation because response data would be missing"
                            : path == "/api/history/executions/run-held/close"
                                ? "Resolve existing retry or checkpoint recovery first"
                                : "History unavailable in legacy Json mode"
                    })
                };
            if (request.Method == HttpMethod.Post)
            {
                if (path == "/api/history/executions/run-held/close")
                {
                    _closed = true;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { closed = true }) };
                }
                _confirmed = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { confirmed = true }) };
            }

            var ticks = new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc).Ticks;
            var execution = new HistoryExecutionViewModel("run-new", "orders-flow", "Completed", ticks, ticks);
            object payload = path switch
            {
                "/api/history/executions" => new[] { execution with { Id = "run-old", StartedUtcTicks = ticks - 1 }, execution },
                "/api/history/executions/run-held" => new HistoryExecutionDetailViewModel(
                    execution with { Id = "run-held", State = _closed ? "Closed" : "Held" }, [], [], []),
                "/api/history/executions/run-new" => new HistoryExecutionDetailViewModel(execution,
                    [new(Guid.NewGuid(), "parse-stage", "Succeeded", ticks, "Parsed 2 records")],
                    [new(ExecutionMessageId, "execution-channel", "Consumed", "run-new", ticks)],
                    [new(UnknownDeliveryId, "delivery-stage", _confirmed ? "Confirmed" : "Unknown", ticks),
                     new(CompletedDeliveryId, "completed-stage", "Succeeded", ticks)]),
                "/api/history/messages" => new HistoryMessageViewModel[]
                {
                    new(InboxId, "inbox-channel", "Pending", null, ticks),
                    new(Guid.NewGuid(), "claimed-channel", "Claimed", null, ticks),
                    new(Guid.NewGuid(), "held-channel", "Held", null, ticks)
                },
                var value when value == $"/api/history/messages/{InboxId:D}" || value == $"/api/history/messages/{ExecutionMessageId:D}"
                    => new HistoryMessagePayloadViewModel($"{{\"secret\":\"{Secret}<script>\"}}"),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(payload, payload.GetType()) };
        }
    }
}
