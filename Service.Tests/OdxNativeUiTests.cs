using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;

namespace Service.Tests;

public sealed class OdxNativeUiTests
{
    [Fact]
    public async Task NativeMapChoicesUseTheVisualEditorWithoutMisleadingMetadataGroupingControls()
    {
        var settings = new Dictionary<string, string> { ["casesJson"] = "[]" };
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<RenderDispatchEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(RenderDispatchEditor.Settings)] = settings, [nameof(RenderDispatchEditor.OdxMode)] = true
            }));
            var html = rendered.ToHtmlString();
            Assert.Contains("ODX transform statements", html); Assert.Contains("Add renderer choice", html);
            Assert.DoesNotContain("Selection metadata key", html); Assert.DoesNotContain("Renderer input grouping", html);
            Assert.Equal("[]", settings["casesJson"]);
        });
    }
}
