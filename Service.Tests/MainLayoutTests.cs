using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Mulse.Ui.Components.Layout;

namespace Service.Tests;

public sealed class MainLayoutTests
{
    [Fact]
    public async Task Menu_CollapsesAndExpandsWithoutLosingLinksBodyThemeOrNavigationState()
    {
        var activator = new CapturingActivator();
        var navigation = new TestNavigation();
        await using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IComponentActivator>(activator)
            .AddSingleton<NavigationManager>(navigation).AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<MainLayout>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(MainLayout.Body)] = (RenderFragment)(builder => builder.AddContent(0, "Current workspace"))
            }));
            var layout = Assert.IsType<MainLayout>(activator.Layout);
            var expanded = rendered.ToHtmlString();
            Assert.Contains("aria-label=\"Collapse main menu\"", expanded);
            Assert.Contains("aria-expanded=\"true\"", expanded);
            Assert.DoesNotContain("sidebar-collapsed", expanded);

            typeof(MainLayout).GetMethod("ToggleMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(layout, null);
            await layout.SetParametersAsync(ParameterView.Empty);
            navigation.NavigateTo("/modules");
            var collapsed = rendered.ToHtmlString();
            Assert.Contains("sidebar-collapsed", collapsed);
            Assert.Contains("aria-label=\"Expand main menu\"", collapsed);
            Assert.Contains("aria-expanded=\"false\"", collapsed);
            Assert.Contains("aria-controls=\"main-menu\"", collapsed);
            Assert.Contains("id=\"main-menu\"", collapsed);
            Assert.Contains("Current workspace", collapsed);
            Assert.Contains("data-theme-toggle", collapsed);
            Assert.Contains("aria-label=\"Toggle dark mode\"", collapsed);
            Assert.Contains("href=\"#main-content\"", collapsed);
            Assert.Matches("<a[^>]*href=\"/modules\"[^>]*class=\"active\"", collapsed);
            foreach (var destination in new[] { ("/", "Dashboard"), ("/config-values", "Config values"), ("/modules", "Modules"), ("/module-packages", "Module Packages"), ("/backup-restore", "Backup &amp; Restore") })
            {
                Assert.Contains($"href=\"{destination.Item1}\"", collapsed);
                Assert.Contains($"aria-label=\"{destination.Item2}\"", collapsed);
                Assert.Contains($"title=\"{destination.Item2}\"", collapsed);
            }

            typeof(MainLayout).GetMethod("ToggleMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(layout, null);
            await layout.SetParametersAsync(ParameterView.Empty);
            Assert.DoesNotContain("sidebar-collapsed", rendered.ToHtmlString());
            Assert.Contains("Current workspace", rendered.ToHtmlString());
            Assert.Contains("aria-label=\"Collapse main menu\"", rendered.ToHtmlString());
        });
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public MainLayout? Layout { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is MainLayout layout) Layout = layout;
            return component;
        }
    }
    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("http://localhost/", "http://localhost/");
        protected override void NavigateToCore(string uri, bool forceLoad)
        {
            Uri = ToAbsoluteUri(uri).ToString();
            NotifyLocationChanged(false);
        }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException("Static rendering must not invoke JS.");
    }
}
