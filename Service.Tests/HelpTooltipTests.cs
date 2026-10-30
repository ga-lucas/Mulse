using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Ui.Components.Shared;

namespace Service.Tests;

public sealed class HelpTooltipTests
{
    [Fact]
    public async Task Help_IsAssociatedWithFocusableTriggerEscapesTextAndCanBeDismissedAndReopened()
    {
        var activator = new CapturingActivator();
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IComponentActivator>(activator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(HelpTooltip.Label)] = "Field paths",
                [nameof(HelpTooltip.ChildContent)] = (RenderFragment)(builder => builder.AddContent(0, "Help with <literal> values"))
            });
            var root = await renderer.RenderComponentAsync<HelpTooltip>(parameters);
            var help = Assert.IsType<HelpTooltip>(activator.Created[0]);
            var html = root.ToHtmlString();
            var id = (string)typeof(HelpTooltip).GetField("_id", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(help)!;
            Assert.Contains("type=\"button\"", html);
            Assert.Contains("aria-label=\"Help: Field paths\"", html);
            Assert.Contains($"aria-describedby=\"{id}\"", html);
            Assert.Contains($"id=\"{id}\"", html);
            Assert.Contains("role=\"tooltip\"", html);
            Assert.Contains("&lt;literal&gt;", html);
            Assert.DoesNotContain("<literal>", html);

            Invoke(help, "Toggle");
            await help.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("pinned", root.ToHtmlString());
            Invoke(help, "HandleKeyDown", new KeyboardEventArgs { Key = "Escape" });
            await help.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("dismissed", root.ToHtmlString());
            Assert.DoesNotContain("pinned", root.ToHtmlString());
            Invoke(help, "ResetDismissal");
            await help.SetParametersAsync(ParameterView.Empty);
            Assert.DoesNotContain("dismissed", root.ToHtmlString());

            var other = await renderer.RenderComponentAsync<HelpTooltip>(parameters);
            Assert.DoesNotContain($"id=\"{id}\"", other.ToHtmlString());
        });
    }

    private static void Invoke(object component, string method, params object[] arguments)
        => component.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, arguments);

    private sealed class CapturingActivator : IComponentActivator
    {
        public List<IComponent> Created { get; } = [];
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            Created.Add(component);
            return component;
        }
    }
}
