using System.Collections;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mulse.Modules.BuiltIn.Augment;
using Mulse.Ui.Components.Shared;

namespace Service.Tests;

public sealed class OrchestrationStateEditorTests
{
    [Fact]
    public async Task VisualCapture_RoundTrips_AndRestoresActualCheckpointMetadata()
    {
        var settings = new Dictionary<string, string>
        {
            ["mode"] = "ResumeOrWait", ["bookmark"] = "next", ["correlationSource"] = "Metadata", ["correlationPath"] = "group"
        };
        var editor = new OrchestrationStateEditor();
        typeof(OrchestrationStateEditor).GetProperty("Settings")!.SetValue(editor, settings);
        Invoke(editor, "OnParametersSet");
        Invoke(editor, "Add");
        var entry = Assert.Single(Assert.IsAssignableFrom<IEnumerable>(Field(editor, "_entries")).Cast<object>());
        entry.GetType().GetProperty("Key")!.SetValue(entry, "map");
        entry.GetType().GetProperty("Source")!.SetValue(entry, "Payload");
        entry.GetType().GetProperty("Path")!.SetValue(entry, "$.map");
        Invoke(editor, "Persist");
        Assert.Null(Field(editor, "_error"));
        var reopened = new OrchestrationStateEditor();
        typeof(OrchestrationStateEditor).GetProperty("Settings")!.SetValue(reopened, new Dictionary<string, string>(settings));
        Invoke(reopened, "OnParametersSet");
        Assert.Single(Assert.IsAssignableFrom<IEnumerable>(Field(reopened, "_entries")).Cast<object>());
        var store = new InMemoryFlowOrchestrationStateStore();
        var module = new StatefulOrchestrationAugmentModule(store, TimeProvider.System, NullLogger<StatefulOrchestrationAugmentModule>.Instance);
        var step = new ModuleStepDefinition { Module = module.Descriptor.Id, Settings = settings };
        var context = new FlowExecutionContext("capture", "first", DateTimeOffset.UtcNow);
        await module.AugmentAsync(context, Input("A", "Map.A"), step, default);
        Assert.Equal(FlowExecutionDisposition.Suspended, context.Disposition);
        Assert.Equal("Map.A", (await store.GetAsync("capture", "A", default))!.StateValues["map"]);
        var resumed = new FlowExecutionContext("capture", "second", DateTimeOffset.UtcNow);
        var output = await module.AugmentAsync(resumed, Input("A", "Map.B"), step, default);
        Assert.True(resumed.IsResumedOrchestration);
        Assert.Equal(2, output.Count);
        Assert.All(output.Payloads, payload => Assert.Equal("Map.A", payload.Metadata["orchestration.state.map"]));
        Assert.Null(await store.GetAsync("capture", "B", default));
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<OrchestrationStateEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Settings"] = settings }));
            Assert.Contains("Checkpoint state to remember", rendered.ToHtmlString());
            Assert.Contains("Checkpoint value source", rendered.ToHtmlString());
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"key\":\"\",\"source\":\"Literal\"}]")]
    [InlineData("[{\"key\":\"correlationKey\",\"source\":\"Literal\"}]")]
    [InlineData("[{\"key\":\"a\",\"source\":\"Metadata\"}]")]
    [InlineData("[{\"key\":\"a\",\"source\":\"Literal\"},{\"key\":\"A\",\"source\":\"Literal\"}]")]
    [InlineData("[{\"key\":\"a\",\"source\":99}]")]
    [InlineData("[{\"key\":\"a\",\"future\":true}]")]
    public async Task InvalidCaptures_FailBeforePersistingCheckpoint(string json)
    {
        var store = new InMemoryFlowOrchestrationStateStore();
        var module = new StatefulOrchestrationAugmentModule(store, TimeProvider.System, NullLogger<StatefulOrchestrationAugmentModule>.Instance);
        await Assert.ThrowsAsync<ArgumentException>(() => module.AugmentAsync(new("invalid", "test", DateTimeOffset.UtcNow), Input("A", "Map.A"), new()
        {
            Settings = new() { ["mode"] = "Suspend", ["bookmark"] = "next", ["correlationSource"] = "Metadata", ["correlationPath"] = "group", ["stateCaptureJson"] = json }
        }, default));
        Assert.Null(await store.GetAsync("invalid", "A", default));
    }

    [Fact]
    public void UnknownJson_IsPreservedForRepair_AndInvalidEditsAreNotSilentlyDiscarded()
    {
        const string original = "[{\"future\":true}]";
        var settings = new Dictionary<string, string> { ["stateCaptureJson"] = original };
        var editor = new OrchestrationStateEditor();
        typeof(OrchestrationStateEditor).GetProperty("Settings")!.SetValue(editor, settings);
        Invoke(editor, "OnParametersSet");
        Assert.Equal(true, Field(editor, "_loadFailed"));
        Assert.Equal(original, settings["stateCaptureJson"]);
        Invoke(editor, "ReplaceJson", new ChangeEventArgs { Value = "[]" });
        Invoke(editor, "Add");
        var entry = Assert.Single(Assert.IsAssignableFrom<IEnumerable>(Field(editor, "_entries")).Cast<object>());
        entry.GetType().GetProperty("Key")!.SetValue(entry, "correlationKey");
        Invoke(editor, "Persist");
        Assert.Contains("correlationKey", settings["stateCaptureJson"]);
        Assert.Contains("Invalid captures cannot execute", Assert.IsType<string>(Field(editor, "_error")));
    }

    [Theory]
    [InlineData("[{\"key\":\"correlationKey\",\"source\":\"Literal\"}]")]
    [InlineData("[{\"key\":\"value\",\"source\":\"Metadata\"}]")]
    public void InvalidSavedCaptures_ReportErrorsOnOpening_WithoutRewriting(string json)
    {
        var settings = new Dictionary<string, string> { ["stateCaptureJson"] = json };
        var editor = new OrchestrationStateEditor();
        typeof(OrchestrationStateEditor).GetProperty("Settings")!.SetValue(editor, settings);
        Invoke(editor, "OnParametersSet");
        Assert.Contains("Invalid captures cannot execute", Assert.IsType<string>(Field(editor, "_error")));
        Assert.Equal(json, settings["stateCaptureJson"]);
    }

    [Fact]
    public void RuntimeCompatibleSourceCasing_IsNormalizedVisually_NotSilentlySaved()
    {
        const string json = "[{\"key\":\"client\",\"source\":\"metadata\",\"path\":\"group\"}]";
        var settings = new Dictionary<string, string> { ["stateCaptureJson"] = json };
        var editor = new OrchestrationStateEditor();
        typeof(OrchestrationStateEditor).GetProperty("Settings")!.SetValue(editor, settings);
        Invoke(editor, "OnParametersSet");
        Assert.Null(Field(editor, "_error"));
        var entry = Assert.Single(Assert.IsAssignableFrom<IEnumerable>(Field(editor, "_entries")).Cast<object>());
        Assert.Equal("Metadata", entry.GetType().GetProperty("Source")!.GetValue(entry));
        Assert.Equal(json, settings["stateCaptureJson"]);
    }

    private static IntegrationBatch Input(string group, string map) => new([new("facts.json", BinaryData.FromString("{\"map\":\"" + map + "\"}"), "application/json", new Dictionary<string, string> { ["group"] = group })]);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static object? Field(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
}
