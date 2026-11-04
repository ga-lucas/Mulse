using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Decisions;
using Mulse.Modules.Mapping;
using Mulse.Ui.Components.Shared;

namespace Service.Tests;

public sealed class MetadataPromotionEditorTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task VisualPromotion_SavesReopens_AndFeedsPolicyThenRealMap_WithoutChangingInput()
    {
        var settings = new Dictionary<string, string>();
        var editor = Open(settings);
        Invoke(editor, "Add");
        var entry = Assert.Single(Entries(editor));
        Set(entry, "MetadataKey", "rules.GroupID");
        Set(entry, "Selector", "/s:Order/s:Group");
        Invoke(editor, "Persist");
        Field(editor, "_newPrefix", "s"); Field(editor, "_newUri", "urn:order");
        Invoke(editor, "AddNamespace");
        Assert.Null(Read(editor, "_error"));
        var saved = JsonSerializer.Serialize(new ModuleStepDefinition { Module = "metadata-promotion-augment", Settings = settings }, Options);
        var step = JsonSerializer.Deserialize<ModuleStepDefinition>(saved, Options)!;
        var reopenedEntry = Assert.Single(Entries(Open(step.Settings)));
        Assert.Equal("rules.GroupID", reopenedEntry.GetType().GetProperty("MetadataKey")!.GetValue(reopenedEntry));
        var original = new IntegrationPayload("order.xml", BinaryData.FromString("<Order xmlns='urn:order'><Group>A</Group><Id>42</Id></Order>"), "application/xml", new Dictionary<string, string>());
        var context = new FlowExecutionContext("visual-policy-map", "test", DateTimeOffset.UtcNow);
        var promoted = await new MetadataPromotionAugmentModule().AugmentAsync(context, new([original]), step, default);
        Assert.Same(original.Content, promoted.Payloads[0].Content);
        Assert.Equal("A", promoted.Payloads[0].Metadata["rules.GroupID"]);
        Assert.False(original.Metadata.ContainsKey("rules.GroupID"));
        var policy = new XmlRulePolicy
        {
            Name = "Client routing", Version = "1.0", RootName = "Facts", Rules = [new()
            {
                Name = "Client A", Conditions = [new() { Name = "GroupID", Value = "A" }],
                Assignments = [new() { Name = "MapName", Value = "invoice" }]
            }]
        };
        var selected = await new XmlRulesAugmentModule().AugmentAsync(context, promoted, new() { Settings = new()
        {
            ["factMode"] = "Metadata", ["policyJson"] = JsonSerializer.Serialize(policy, Options)
        } }, default);
        var catalog = new FakeExecutableModuleCatalog().AddRender(new XmlMappingRenderModule());
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog)
            .AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>().BuildServiceProvider();
        var mapping = new XmlMappingDefinition
        {
            TargetRoot = "Invoice", Namespaces = new() { ["s"] = "urn:order" },
            Fields = [new() { SourcePath = "/s:Order/s:Id", TargetPath = "Id" }]
        };
        var result = await new RenderDispatchModule(services.GetRequiredService<IRenderModuleInvoker>()).RenderAsync(context, selected,
            new() { Settings = new() { ["metadataKey"] = "rules.MapName", ["casesJson"] = JsonSerializer.Serialize(new[]
            {
                new RenderDispatchCase { Value = "invoice", Module = "xml-mapping-render", Settings = new() { ["mappingJson"] = XmlMappingTests.Json(mapping) } }
            }, Options) } }, default);
        Assert.Equal("42", XDocument.Parse(Assert.Single(result.Payloads).GetText()).Root!.Element("Id")!.Value);
        await using var renderingServices = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime>(new NoJs()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(renderingServices, renderingServices.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<MetadataPromotionEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Settings"] = step.Settings }));
            var html = rendered.ToHtmlString();
            Assert.Contains("Promote message fields", html);
            Assert.Contains("/s:Order/s:Group", html);
            Assert.Contains("urn:order", html);
            Assert.DoesNotContain("entry.Selector", html);
            Assert.DoesNotContain("_namespaces[prefix]", html);
        });
    }

    [Theory]
    [InlineData(false, "existing")]
    [InlineData(true, "A|B")]
    public async Task VisualJsonPromotion_PreservesJoinDefaultAndOverwriteBehavior(bool overwrite, string expected)
    {
        var settings = new Dictionary<string, string> { ["overwriteExisting"] = overwrite.ToString() };
        var editor = Open(settings);
        Invoke(editor, "Add");
        var entry = Assert.Single(Entries(editor));
        Set(entry, "MetadataKey", "groups"); Set(entry, "Selector", "$.groups[]"); Set(entry, "JoinWith", "|");
        Invoke(editor, "Persist");
        Invoke(editor, "Add");
        entry = Entries(editor).Last();
        Set(entry, "MetadataKey", "fallback"); Set(entry, "Selector", "$.missing"); Set(entry, "DefaultValue", "Unknown");
        Invoke(editor, "Persist");
        var original = new IntegrationPayload("facts.json", BinaryData.FromString("{\"groups\":[\"A\",\"B\"]}"), "application/json", new Dictionary<string, string> { ["groups"] = "existing" });
        var output = await new MetadataPromotionAugmentModule().AugmentAsync(new("test", "test", DateTimeOffset.UtcNow), new([original]), new() { Settings = settings }, default);
        Assert.Equal(expected, output.Payloads[0].Metadata["groups"]);
        Assert.Equal("Unknown", output.Payloads[0].Metadata["fallback"]);
        Assert.Equal("existing", original.Metadata["groups"]);
    }

    [Theory]
    [InlineData("null", "{}")]
    [InlineData("[null]", "{}")]
    [InlineData("[{\"futureProperty\":true}]", "{}")]
    [InlineData("[]", "{\"s\":null}")]
    [InlineData("[]", "{\"s\":4}")]
    public void UnsupportedDefinitions_ArePreservedForAdvancedRepair(string promotions, string namespaces)
    {
        var settings = new Dictionary<string, string> { ["promotionsJson"] = promotions, ["namespacesJson"] = namespaces };
        var editor = Open(settings);
        Assert.Equal(true, Read(editor, "_loadFailed"));
        Assert.Equal(promotions, settings["promotionsJson"]);
        Assert.Equal(namespaces, settings["namespacesJson"]);
        Assert.Contains("has not been replaced", Assert.IsType<string>(Read(editor, "_error")));
        Invoke(editor, "ReplacePromotions", new ChangeEventArgs { Value = "[]" });
        Invoke(editor, "ReplaceNamespaces", new ChangeEventArgs { Value = "{}" });
        Assert.Equal(false, Read(editor, "_loadFailed"));
        Invoke(editor, "Add");
        Assert.Contains("Incomplete promotions cannot execute", Assert.IsType<string>(Read(editor, "_error")));
        Assert.Contains("metadataKey", settings["promotionsJson"]);
    }

    [Fact]
    public void NamespaceEditing_RejectsReservedDuplicateOrInvalidPrefixes_AndRemovesExplicitly()
    {
        var settings = new Dictionary<string, string>();
        var editor = Open(settings);
        Field(editor, "_newUri", "urn:order");
        foreach (var invalid in new[] { "", "bad prefix", "xml", "xmlns" })
        {
            Field(editor, "_newPrefix", invalid); Invoke(editor, "AddNamespace");
            Assert.NotNull(Read(editor, "_error"));
            Assert.False(settings.ContainsKey("namespacesJson"));
        }
        Field(editor, "_newPrefix", "s"); Invoke(editor, "AddNamespace");
        Assert.Contains("urn:order", settings["namespacesJson"]);
        Field(editor, "_newPrefix", "s"); Field(editor, "_newUri", "urn:changed"); Invoke(editor, "AddNamespace");
        Assert.DoesNotContain("urn:changed", settings["namespacesJson"]);
        Field(editor, "_newPrefix", "S"); Invoke(editor, "AddNamespace");
        Assert.NotNull(Read(editor, "_error"));
        Assert.DoesNotContain("urn:changed", settings["namespacesJson"]);
        Invoke(editor, "SetNamespace", "s", "urn:changed");
        Assert.Contains("urn:changed", settings["namespacesJson"]);
        Invoke(editor, "RemoveNamespace", "s"); Assert.Equal("{}", settings["namespacesJson"]);
    }

    [Fact]
    public void VisualRowLimitIsExact_AndOversizedOriginalJsonIsNotReplaced()
    {
        var settings = new Dictionary<string, string>();
        var editor = Open(settings);
        for (var index = 0; index < 512; index++) Invoke(editor, "Add");
        Assert.Equal(512, Entries(editor).Length);
        var saved = settings["promotionsJson"];
        Invoke(editor, "Add");
        Assert.Equal(saved, settings["promotionsJson"]);
        Assert.Contains("at most 512", Assert.IsType<string>(Read(editor, "_error")));
        Invoke(editor, "ReplacePromotions", new ChangeEventArgs { Value = new string(' ', 1000001) });
        Assert.Equal(true, Read(editor, "_loadFailed"));
        Assert.Equal(1000001, settings["promotionsJson"].Length);
    }

    private static MetadataPromotionEditor Open(Dictionary<string, string> settings)
    {
        var editor = new MetadataPromotionEditor(); Set(editor, "Settings", settings); Invoke(editor, "OnParametersSet"); return editor;
    }
    private static object[] Entries(object editor) => Assert.IsAssignableFrom<IEnumerable>(Read(editor, "_entries")).Cast<object>().ToArray();
    private static void Set(object target, string name, object value) => target.GetType().GetProperty(name)!.SetValue(target, value);
    private static void Field(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static object? Read(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
    private static object? Invoke(object target, string name, params object?[] args) => target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
}
