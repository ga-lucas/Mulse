using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.Modules.ControlFlow;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;

namespace Service.Tests;

public sealed class NativeDurableUiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128, Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task VisualDurableReceiveMessageWaitAndDeliveryControlsSaveAndReopenNativeJson()
    {
        var model = new NativeFlowEditorModel { Durable = true };
        var editor = new NativeFlowNodesEditor();
        Set(editor, nameof(editor.Nodes), model.Nodes);
        Set(editor, nameof(editor.Modules), new ModuleViewModel[] { new("sink", "Synthetic sink", "Deliver", "Test", [], [], [], []) });
        string saved = "";
        Set(editor, nameof(editor.Changed), EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(editor, "Add"); var receive = model.Nodes.Single();
        await Invoke(editor, "ChangeKind", receive, "Receive"); receive.Channel = "input"; receive.MessageName = "request";
        await Invoke(editor, "AddCorrelation", receive); await Invoke(editor, "ChangeCorrelation", receive, 0, "partner");
        await Invoke(editor, "Add"); var wait = model.Nodes.Last(); await Invoke(editor, "ChangeKind", wait, "Wait"); wait.DurationSeconds = 10;
        await Invoke(editor, "Add"); var deliver = model.Nodes.Last(); await Invoke(editor, "ChangeKind", deliver, "Deliver");
        deliver.MessageName = "request"; await Invoke(editor, "ChangeModule", deliver, "sink");
        await Invoke(editor, "Add"); var reply = model.Nodes.Last(); await Invoke(editor, "ChangeKind", reply, "Reply"); reply.MessageName = "request";
        await Invoke(editor, "Notify");
        var definition = NativeFlowDefinition.Read(saved);
        Assert.True(definition.Durable); Assert.Equal("partner", Assert.Single(definition.Nodes[0].CorrelationKeys));
        Assert.Equal(10, definition.Nodes[1].DurationSeconds); Assert.Equal("sink", definition.Nodes[2].Module);
        var html = await Render(saved, editor.Modules);
        Assert.Contains("Durable execution", html); Assert.Contains("Instance metadata key", html); Assert.Contains("Stable message ID metadata key", html);
        Assert.Contains("Incoming channel", html); Assert.Contains("Pinned correlation metadata key", html); Assert.Contains("Wait seconds", html);
        Assert.Contains("Synthetic sink", html); Assert.Contains("Native named message", html); Assert.Contains("request", html);
        Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete", html);
        await Invoke(editor, "RemoveCorrelation", receive, 0); Assert.Empty(NativeFlowDefinition.Read(saved).Nodes[0].CorrelationKeys);
    }

    [Fact]
    public async Task ReceiveWithoutDurableModeOrRequiredIdentityIsVisiblyIncomplete()
    {
        var json = JsonSerializer.Serialize(new NativeFlowEditorModel { Nodes = [new() { Kind = "Receive" }] }, Json);
        Assert.Contains("Some nodes are incomplete", await Render(json, []));
    }

    [Fact]
    public async Task ScratchXmlConstructionAndWriteTargetsCanBeAuthoredWithGraphicalCallbacksAndSampleClicks()
    {
        var model = new NativeFlowEditorModel { Durable = true };
        var editor = new NativeFlowNodesEditor(); Set(editor, nameof(editor.Nodes), model.Nodes);
        string saved = "";
        var changed = EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json));
        Set(editor, nameof(editor.Changed), changed);
        await Invoke(editor, "Add"); var create = model.Nodes.Single();
        await Invoke(editor, "ChangeKind", create, "CreateXmlMessage");
        create.MessageName = "created"; create.Expression!.Literal!.Value = "<r><value>initial</value></r>";
        await Invoke(editor, "Add"); var write = model.Nodes.Last();
        await Invoke(editor, "ChangeKind", write, "SetMessageValue");
        write.Target!.MessageName = "created"; write.Expression!.Literal!.Value = "updated";
        var target = new NativeMessageFactEditor(); Set(target, nameof(target.Value), write.Target);
        Set(target, nameof(target.Writable), true); Set(target, nameof(target.Changed), changed);
        await Invoke(target, "ChangeKind", new ChangeEventArgs { Value = "XmlText" });
        Assert.Equal("XmlString", write.Target.Kind);
        target.GetType().GetField("_sample", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, "<r flag='private'><value>sample-only</value><complex><child/></complex></r>");
        target.GetType().GetMethod("InspectSample", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, null);
        var paths = (List<List<NativeXmlStepViewModel>>)target.GetType().GetField("_paths", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
        Assert.All(paths, path => Assert.DoesNotContain(path, step => step.Attribute));
        Assert.DoesNotContain(paths, path => path.Count == 1 || path[^1].Name == "complex");
        await Invoke(target, "SelectPath", paths.Single(path => path.Count == 2 && path[^1].Name == "value"));
        var definition = NativeFlowDefinition.Read(saved);
        Assert.Equal(NativeFlowNodeKind.CreateXmlMessage, definition.Nodes[0].Kind);
        Assert.Equal("created", definition.Nodes[1].Target!.MessageName);
        Assert.Equal(NativeMessageFactKind.XmlString, definition.Nodes[1].Target!.Kind);
        Assert.DoesNotContain("sample-only", saved); Assert.DoesNotContain("private", saved);
        var html = await Render(saved, []);
        Assert.Contains("Write target", html); Assert.Contains("Choose XML path from a sample", html);
        Assert.DoesNotContain("Some nodes are incomplete", html); Assert.DoesNotContain("Original JSON is preserved", html);
        await Invoke(target, "ChangeKind", new ChangeEventArgs { Value = "Metadata" });
        write.Target.MetadataKey = "explicit-key"; await Invoke(editor, "Notify");
        Assert.Empty(NativeFlowDefinition.Read(saved).Nodes[1].Target!.Steps);
        await Invoke(editor, "ChangeKind", write, "Sequence");
        Assert.Null(NativeFlowDefinition.Read(saved).Nodes[1].Target);
    }

    private static void Set(object instance, string name, object value) => instance.GetType().GetProperty(name)!.SetValue(instance, value);
    private static async Task Invoke(object instance, string method, params object?[] arguments)
        => await (Task)instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments)!;
    private static async Task<string> Render(string json, IReadOnlyList<ModuleViewModel> modules)
    {
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?>
            { ["Value"] = json, ["Modules"] = modules }))).ToHtmlString());
    }
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
}
