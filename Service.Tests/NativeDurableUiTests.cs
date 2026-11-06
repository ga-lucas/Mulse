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
    public async Task TerminateCanBeEditedSavedAndReopened()
    {
        var model = new NativeFlowEditorModel { Durable = true };
        var editor = new NativeFlowNodesEditor(); Set(editor, nameof(editor.Nodes), model.Nodes);
        string saved = "";
        Set(editor, nameof(editor.Changed), EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(editor, "Add"); var node = model.Nodes.Single();
        await Invoke(editor, "ChangeKind", node, "Terminate"); node.TerminateMessage = "Stop here"; await Invoke(editor, "Notify");
        Assert.Equal("Stop here", NativeFlowDefinition.Read(saved).Nodes.Single().TerminateMessage);
        var html = await Render(saved, []);
        Assert.Contains("Termination message", html); Assert.Contains("Stop here", html);
        Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete", html);
        node.TerminateMessage = new string('x', 2001); await Invoke(editor, "Notify");
        Assert.Contains("Some nodes are incomplete", await Render(saved, []));
        await Invoke(editor, "ChangeKind", node, "Sequence");
        Assert.Null(NativeFlowDefinition.Read(saved).Nodes.Single().TerminateMessage);
    }

    [Fact]
    public async Task TryCanBeEditedSavedAndReopened()
    {
        var model = new NativeFlowEditorModel { Durable = true };
        var editor = new NativeFlowNodesEditor(); Set(editor, nameof(editor.Nodes), model.Nodes);
        string saved = "";
        Set(editor, nameof(editor.Changed), EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(editor, "Add"); var node = model.Nodes.Single();
        await Invoke(editor, "ChangeKind", node, "Try");
        await Invoke(editor, "SetExceptionVariable", node, false, "failure");
        await Invoke(editor, "SetExceptionVariable", node, true, "failureDetails");
        node.Children.Add(new() { Kind = "Terminate", TerminateMessage = "body" });
        node.Otherwise.Add(new() { Kind = "Terminate", TerminateMessage = "handler" });
        await Invoke(editor, "Notify");
        var read = NativeFlowDefinition.Read(saved).Nodes.Single();
        Assert.Equal(NativeFlowNodeKind.Try, read.Kind);
        Assert.Equal("failure", read.ExceptionMessageVariable); Assert.Equal("failureDetails", read.ExceptionDetailsVariable);
        Assert.Single(read.Children); Assert.Single(read.Otherwise);
        var html = await Render(saved, []);
        Assert.Contains("Catch handler", html); Assert.Contains("Exception message variable", html);
        Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete", html);
        await Invoke(editor, "SetExceptionVariable", node, true, "failure");
        Assert.Contains("Some nodes are incomplete", await Render(saved, []));
        await Invoke(editor, "ChangeKind", node, "Sequence"); node.Otherwise.Clear(); await Invoke(editor, "Notify");
        Assert.Null(NativeFlowDefinition.Read(saved).Nodes.Single().ExceptionMessageVariable);
    }

    [Fact]
    public async Task InvokeTransformCanBeEditedSavedAndReopenedAndValidatesItsModuleKind()
    {
        var model = new NativeFlowEditorModel { Durable = true };
        var modules = new ModuleViewModel[] { new("upper-udf", "Upper UDF", "Transform", "Test", [], [], [], []), new("sink", "Sink", "Deliver", "Test", [], [], [], []) };
        var editor = new NativeFlowNodesEditor(); Set(editor, nameof(editor.Nodes), model.Nodes); Set(editor, nameof(editor.Modules), modules);
        string saved = "";
        Set(editor, nameof(editor.Changed), EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(editor, "Add"); var declare = model.Nodes.Single();
        await Invoke(editor, "ChangeKind", declare, "DeclareVariable"); declare.Variable = "name"; declare.Expression!.Literal!.Value = "";
        await Invoke(editor, "Add"); var node = model.Nodes.Last();
        await Invoke(editor, "ChangeKind", node, "InvokeTransform"); node.Variable = "name";
        await Invoke(editor, "ChangeModule", node, "upper-udf");
        node.TransformInputs![0].Literal!.Value = "abc";
        await Invoke(editor, "AddTransformInput", node); node.TransformInputs[1].Literal!.Value = "def";
        await Invoke(editor, "Notify");
        var read = NativeFlowDefinition.Read(saved).Nodes[1];
        Assert.Equal(NativeFlowNodeKind.InvokeTransform, read.Kind);
        Assert.Equal("upper-udf", read.Module); Assert.Equal("name", read.Variable); Assert.Equal(2, read.TransformInputs!.Count);
        var html = await Render(saved, modules);
        Assert.Contains("Result variable", html); Assert.Contains("Argument 2", html); Assert.Contains("Upper UDF", html);
        Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete", html);
        await Invoke(editor, "RemoveTransformInput", node, 1); Assert.Single(NativeFlowDefinition.Read(saved).Nodes[1].TransformInputs!);
        await Invoke(editor, "ChangeModule", node, "sink");
        Assert.Contains("Some nodes are incomplete", await Render(saved, modules));
        await Invoke(editor, "ChangeKind", node, "Sequence");
        var reopened = NativeFlowDefinition.Read(saved).Nodes[1];
        Assert.Null(reopened.TransformInputs); Assert.Null(reopened.Variable);
    }

    [Fact]
    public async Task DeliverDynamicSettingsCanBeAddedRenamedRemovedAndReopened()
    {
        var model = new NativeFlowEditorModel { Durable = true };
        var settings = new ModuleSettingViewModel[] { new("host", "Host", "", true, "Text", "default-host", []), new("remotePath", "Remote path", "", false, "Text", "/", []) };
        var modules = new ModuleViewModel[] { new("sink", "Sink", "Deliver", "Test", settings, [], [], []) };
        var editor = new NativeFlowNodesEditor(); Set(editor, nameof(editor.Nodes), model.Nodes); Set(editor, nameof(editor.Modules), modules);
        string saved = "";
        Set(editor, nameof(editor.Changed), EventCallback.Factory.Create(new object(), () => saved = JsonSerializer.Serialize(model, Json)));
        await Invoke(editor, "Add"); var node = model.Nodes.Single();
        await Invoke(editor, "ChangeKind", node, "Deliver"); await Invoke(editor, "ChangeModule", node, "sink"); node.MessageName = "source";
        Assert.True(node.Settings.ContainsKey("host"));
        await Invoke(editor, "AddSettingExpression", node);
        Assert.False(node.Settings.ContainsKey("host")); Assert.True(node.SettingExpressions!.ContainsKey("host"));
        node.SettingExpressions["host"].Literal!.Value = "sftp.example.test"; await Invoke(editor, "Notify");
        var read = NativeFlowDefinition.Read(saved).Nodes.Single();
        Assert.True(read.SettingExpressions!.ContainsKey("host")); Assert.False(read.Settings.ContainsKey("host"));
        var html = await Render(saved, modules);
        Assert.Contains("Dynamic settings", html); Assert.DoesNotContain("Original JSON is preserved", html); Assert.DoesNotContain("Some nodes are incomplete", html);
        await Invoke(editor, "RenameSettingExpression", node, "host", "remotePath");
        Assert.True(node.SettingExpressions.ContainsKey("remotePath")); Assert.False(node.Settings.ContainsKey("remotePath"));
        await Invoke(editor, "RemoveSettingExpression", node, "remotePath");
        Assert.Null(node.SettingExpressions); Assert.Equal("/", node.Settings["remotePath"]);
        Assert.Null(NativeFlowDefinition.Read(saved).Nodes.Single().SettingExpressions);
    }

    [Fact]
    public async Task NamedAugmentCanBeEditedAndReopened()
    {
        var model = new NativeFlowEditorModel { Durable = true, Nodes = [new() { Kind = "Augment", Module = "augment" }] };
        var node = model.Nodes.Single(); var editor = new NativeFlowNodesEditor(); Set(editor, nameof(editor.Nodes), model.Nodes);
        await Invoke(editor, "ChangeRenderMode", node, "Named"); await Invoke(editor, "ChangeRenderInput", node, 0, "source");
        node.MessageName = "output";
        var saved = JsonSerializer.Serialize(model, Json);
        Assert.Equal("source", Assert.Single(NativeFlowDefinition.Read(saved).Nodes.Single().InputMessages!));
        var modules = new ModuleViewModel[] { new("augment", "Test augment", "OrchestrationAugment", "Test", [], [], [], []) };
        var html = await Render(saved, modules);
        Assert.Contains("Augment message mode", html); Assert.Contains("One named input to named output", html);
        Assert.DoesNotContain("Some nodes are incomplete", html); Assert.DoesNotContain("Original JSON is preserved", html);
        node.InputMessages!.Add("another");
        Assert.Contains("Some nodes are incomplete", await Render(JsonSerializer.Serialize(model, Json), modules));
        await Invoke(editor, "ChangeRenderMode", node, "Working");
        Assert.Null(node.InputMessages); Assert.Equal("", node.MessageName);
    }

    [Fact]
    public async Task NestedBlockersAreListedWithLinksToTheirEditableNodes()
    {
        var model = new NativeFlowEditorModel { Durable = true, Nodes = [new() { Id = "outer", Kind = "If", Name = "Choice", Conditions = [new()], Children = [
            new() { Id = "inner", Kind = "Blocked", Name = "Unsupported delay", BlockReason = "Needs a duration" }], Otherwise = [
            new() { Id = "other", Kind = "Blocked", Name = "Unsupported delay", BlockReason = "Needs another duration" }] }] };
        var html = await Render(JsonSerializer.Serialize(model, Json), []);
        Assert.Contains("Unresolved native nodes (2)", html);
        Assert.Contains("Choice / Then / Unsupported delay", html); Assert.Contains("Choice / Else / Unsupported delay", html);
        Assert.Contains("(inner)", html); Assert.Contains("(other)", html); Assert.Contains("Needs a duration", html);
        var match = System.Text.RegularExpressions.Regex.Match(html, "href=\"#(?<id>native-node-[a-f0-9]+-inner)\"");
        Assert.True(match.Success, html);
        Assert.Contains("id=\"" + match.Groups["id"].Value + "\"", html);
        Assert.Contains("execution is not approved by editing", html);
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
