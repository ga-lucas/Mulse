using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Mulse.Ui.Components.Shared;

namespace Service.Tests;

public sealed class ReferencePickerUiTests
{
    [Fact]
    public async Task Picker_ListsAllNamesAndSupportsSearchAndNamespaceWithoutChangingLiteral()
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            ReferenceInput? picker = null;
            var rendered = await renderer.RenderComponentAsync<PickerHost>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(PickerHost.Capture)] = (Action<ReferenceInput>)(value => picker = value)
            }));
            Assert.NotNull(picker);
            Set(picker, "_showPicker", true);
            await picker.SetParametersAsync(ParameterView.Empty);
            var html = System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("Stored reference picker", html);
            Assert.Contains("Find reference", html);
            Assert.Contains("{{config:name-44}}", html);
            Assert.Contains("Only names are shown", html);
            Assert.DoesNotContain("{{config:bad{key}}", html);
            Set(picker, "_referenceSearch", "NAME-43"); Set(picker, "_referenceKind", "secret");
            await picker.SetParametersAsync(ParameterView.Empty);
            html = System.Net.WebUtility.HtmlDecode(rendered.ToHtmlString());
            Assert.Contains("{{secret:name-43}}", html);
            Assert.DoesNotContain("{{config:name-43}}", html);
            Assert.DoesNotContain("{{secret:name-44}}", html);
            Assert.Equal("literal preserved", picker.Value);
            Set(picker, "_referenceSearch", "missing");
            await picker.SetParametersAsync(ParameterView.Empty);
            Assert.Contains("No matching stored references", rendered.ToHtmlString());
            Assert.Contains("href=\"/config-values\"", rendered.ToHtmlString());
        });
    }

    [Theory]
    [InlineData("Boolean")]
    [InlineData("Select")]
    public async Task ChoiceReferenceMode_OpensPickerAndLiteralModeClosesIt(string kind)
    {
        var picker = new ReferenceInput();
        typeof(ReferenceInput).GetProperty(nameof(ReferenceInput.InputKind))!.SetValue(picker, kind);
        await (Task)typeof(ReferenceInput).GetMethod("UseReferenceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null)!;
        Assert.True((bool)typeof(ReferenceInput).GetField("_showPicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!);
        await (Task)typeof(ReferenceInput).GetMethod("UseLiteralAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, null)!;
        Assert.False((bool)typeof(ReferenceInput).GetField("_showPicker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker)!);
    }

    [Fact]
    public async Task PickerInsertion_AwaitsTheBindingUpdateAndReportsUnavailableEditors()
    {
        var picker = new ReferenceInput();
        string? published = null;
        typeof(ReferenceInput).GetProperty(nameof(ReferenceInput.ValueChanged))!.SetValue(picker,
            EventCallback.Factory.Create<string>(new object(), value => published = value));
        async Task Insert() => await (Task)typeof(ReferenceInput).GetMethod("InsertReferenceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(picker, ["name"] )!;
        object? Read(string name) => typeof(ReferenceInput).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(picker);
        Set(picker, "_showPicker", true);
        await Insert();
        Assert.Null(published);
        Assert.Contains("still loading", (string)Read("_referenceError")!);
        Set(picker, "_controller", new PickerController(fail: true));
        await Insert();
        Assert.Null(published);
        Assert.Contains("Unable to insert", (string)Read("_referenceError")!);
        Assert.True((bool)Read("_showPicker")!);
        var controller = new PickerController(fail: false);
        Set(picker, "_controller", controller);
        await Insert();
        Assert.Equal("prefix {{config:name}} suffix", published);
        Assert.Equal("{{config:name}}", Assert.Single(controller.Arguments!));
        Assert.Null(Read("_referenceError"));
        Assert.False((bool)Read("_showPicker")!);
    }

    private sealed class PickerController(bool fail) : IJSObjectReference
    {
        public object?[]? Arguments { get; private set; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args)
        {
            Assert.Equal("insertReference", identifier);
            Arguments = args;
            return fail ? ValueTask.FromException<T>(new JSException("fixture unavailable")) : ValueTask.FromResult((T)(object)"prefix {{config:name}} suffix");
        }

        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("insertReference", identifier);
            Arguments = args;
            return fail ? ValueTask.FromException<T>(new JSException("fixture unavailable")) : ValueTask.FromResult((T)(object)"prefix {{config:name}} suffix");
        }
    }

    private static void Set(ReferenceInput picker, string name, object value)
        => typeof(ReferenceInput).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(picker, value);
    private sealed class PickerHost : ComponentBase
    {
        [Parameter] public Action<ReferenceInput>? Capture { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<ReferenceInput>(0);
            builder.AddAttribute(1, nameof(ReferenceInput.Value), "literal preserved");
            builder.AddAttribute(2, nameof(ReferenceInput.References), Enumerable.Range(0, 45).Select(index => $"name-{index:00}").Append("bad{key").ToArray());
            builder.AddComponentReferenceCapture(3, value => Capture?.Invoke((ReferenceInput)value));
            builder.CloseComponent();
        }
    }
    private sealed class NoJs : IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException("Static render must not invoke JS.");
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException("Static render must not invoke JS.");
    }
}
