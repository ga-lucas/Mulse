using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Pages;
using Mulse.Ui.Models;
using Mulse.Ui.Services;

namespace Service.Tests;

#pragma warning disable BL0006 // Inspect rendered event handlers to exercise real Blazor button clicks.
public sealed class ModulePackageInteractionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unload_RequiresInlineConfirmationThenShowsResult(bool inUse)
    {
        var handler = new PackageHandler(inUse);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IMulseApiClient>(new MulseApiClient(http)).BuildServiceProvider();
        await using var renderer = new PackageRenderer(services);
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var root = await renderer.MountAsync();
            Assert.Contains("Fixture fetch", renderer.Text(root));
            await renderer.ClickAsync(root, "Unload package");
            Assert.Equal(0, handler.UnloadRequests);
            Assert.Contains("Unload fixture?", renderer.Text(root));
            Assert.True(renderer.HasButton(root, "Confirm unload"));
            await renderer.ClickAsync(root, "Cancel");
            Assert.False(renderer.HasButton(root, "Confirm unload"));
            Assert.Equal(0, handler.UnloadRequests);
            await renderer.ClickAsync(root, "Unload package");
            await renderer.ClickAsync(root, "Confirm unload");
            Assert.Equal(1, handler.UnloadRequests);
            if (inUse)
            {
                Assert.Contains("used by saved flows: dependent-flow", renderer.Text(root));
                Assert.True(renderer.HasButton(root, "Unload package"));
            }
            else
            {
                Assert.Contains("Package fixture is now unloaded.", renderer.Text(root));
                Assert.Contains("Module details are unavailable", renderer.Text(root));
                Assert.DoesNotContain("Fixture fetch", renderer.Text(root));
                Assert.True(renderer.HasButton(root, "Load package"));
                Assert.False(renderer.HasButton(root, "Unload package"));
                await renderer.ClickAsync(root, "Load package");
                Assert.Contains("Package fixture is now loaded.", renderer.Text(root));
                Assert.Contains("Fixture fetch", renderer.Text(root));
                Assert.True(renderer.HasButton(root, "Unload package"));
            }
        });
    }

    private sealed class PackageHandler(bool inUse) : HttpMessageHandler
    {
        private bool _loaded = true;
        public int UnloadRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Patch)
            {
                var payload = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                var enabled = payload.GetProperty("enabled").GetBoolean();
                if (!enabled)
                {
                    UnloadRequests++;
                    Assert.Equal("UNLOAD PACKAGE", payload.GetProperty("confirmation").GetString());
                    if (inUse)
                        return new HttpResponseMessage(HttpStatusCode.Conflict)
                        {
                            Content = JsonContent.Create(new { detail = "Package 'fixture' is used by saved flows: dependent-flow." })
                        };
                }
                _loaded = enabled;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Package()) };
            }
            Assert.Equal(HttpMethod.Get, request.Method);
            if (request.RequestUri!.AbsolutePath == "/api/modules")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(_loaded
                        ? new[] { new ModuleViewModel("fixture-fetch", "Fixture fetch", "Fetch", "Reads fixture", [], [], [], [], "fixture", "Managed", "C:\\plugins\\fixture.dll") }
                        : Array.Empty<ModuleViewModel>())
                };
            Assert.Equal("/api/module-packages", request.RequestUri.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { Package() }) };
        }

        private ModulePackageViewModel Package() => new("fixture", "C:\\plugins\\fixture.dll", "Managed", _loaded, DateTimeOffset.MinValue, _loaded ? 1 : 0);
    }

    private sealed class PackageRenderer(IServiceProvider services) : Renderer(services, services.GetRequiredService<ILoggerFactory>())
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override void HandleException(Exception exception) => throw new InvalidOperationException("Package rendering failed.", exception);
        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;

        public async Task<int> MountAsync()
        {
            var root = AssignRootComponentId(InstantiateComponent(typeof(ModulePackages)));
            await RenderRootComponentAsync(root, ParameterView.Empty);
            return root;
        }

        public bool HasButton(int root, string label) => ButtonEvent(root, label) is not null;
        public Task ClickAsync(int root, string label) => DispatchEventAsync(ButtonEvent(root, label)
            ?? throw new InvalidOperationException($"Button '{label}' was not rendered."), null, new MouseEventArgs());

        public string Text(int root) => string.Concat(Frames(root).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => string.Empty
        }));

        private ulong? ButtonEvent(int root, string label)
        {
            foreach (var componentId in ComponentIds(root))
            {
                var frames = GetCurrentRenderTreeFrames(componentId);
                for (var index = 0; index < frames.Count; index++)
                {
                    var frame = frames.Array[index];
                    if (frame.FrameType != RenderTreeFrameType.Element || frame.ElementName != "button") continue;
                    var button = frames.Array.Skip(index + 1).Take(frame.ElementSubtreeLength - 1).ToArray();
                    if (button.Any(child => child.FrameType == RenderTreeFrameType.Text && child.TextContent.Trim() == label))
                        return button.Single(child => child.FrameType == RenderTreeFrameType.Attribute && child.AttributeName == "onclick").AttributeEventHandlerId;
                }
            }
            return null;
        }

        private IEnumerable<RenderTreeFrame> Frames(int root) => ComponentIds(root)
            .SelectMany(id => { var frames = GetCurrentRenderTreeFrames(id); return frames.Array.Take(frames.Count); });

        private IEnumerable<int> ComponentIds(int root)
        {
            yield return root;
            var frames = GetCurrentRenderTreeFrames(root);
            for (var index = 0; index < frames.Count; index++)
                if (frames.Array[index].FrameType == RenderTreeFrameType.Component)
                    foreach (var id in ComponentIds(frames.Array[index].ComponentId)) yield return id;
        }
    }
}
#pragma warning restore BL0006
