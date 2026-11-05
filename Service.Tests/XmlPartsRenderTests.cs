using System.Text.Json;
using System.Xml.Linq;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mulse.CompatibilityPack;
using Mulse.Modules.Abstractions;
using Mulse.Modules.BuiltIn;
using Mulse.Modules.BuiltIn.Render;
using Mulse.Modules.Mapping;
using Mulse.Ui.Components.Shared;
using Mulse.Ui.Models;

namespace Service.Tests;

public sealed class XmlPartsRenderTests
{
    private static readonly FlowExecutionContext Context = new("parts", "synthetic", DateTimeOffset.UtcNow);
    private static ModuleStepDefinition Step(string names = "InputMessagePart_0,InputMessagePart_1") => new() { Module = "xml-parts-render", Settings = new()
    {
        ["rootName"] = "Root", ["rootNamespace"] = "http://schemas.microsoft.com/BizTalk/2003/aggschema", ["partNames"] = names, ["outputName"] = "parts.xml"
    } };
    private static IntegrationBatch Input(params string[] xml) => new(xml.Select((text, index) => new IntegrationPayload(index + ".xml", BinaryData.FromString(text), "application/xml", new Dictionary<string, string> { ["private"] = "not-copied" })).ToArray());

    [Fact]
    public async Task ExplicitAggregateContractPreservesSourceRootsOrderAndInternalXmlWithoutMetadataMerging()
    {
        const string first = "<?xml version='1.0'?>\n<Billing xmlns='urn:billing' flag='A&amp;B'><![CDATA[x<y]]><!--inside--><?test yes?><Id>42</Id></Billing>";
        var input = Input(first, "<MessageIndexer xmlns='urn:index'><Count>2</Count></MessageIndexer>");
        var result = Assert.Single((await new XmlPartsRenderModule().RenderAsync(Context, input, Step(), default)).Payloads);
        var root = XDocument.Parse(result.GetText(), LoadOptions.PreserveWhitespace).Root!;
        Assert.Equal(XName.Get("Root", "http://schemas.microsoft.com/BizTalk/2003/aggschema"), root.Name);
        Assert.Equal(new[] { XName.Get("InputMessagePart_0"), XName.Get("InputMessagePart_1") }, root.Elements().Select(element => element.Name));
        var billing = root.Elements().First().Elements().Single();
        Assert.Equal(XName.Get("Billing", "urn:billing"), billing.Name); Assert.Equal("A&B", billing.Attribute("flag")!.Value);
        Assert.Contains(billing.Nodes(), node => node is XCData); Assert.Contains(billing.Nodes(), node => node is XComment); Assert.Contains(billing.Nodes(), node => node is XProcessingInstruction);
        Assert.Equal(XName.Get("MessageIndexer", "urn:index"), root.Elements().Last().Elements().Single().Name);
        Assert.Empty(result.Metadata); Assert.Equal("parts.xml", result.Name); Assert.Equal("application/xml", result.ContentType);
        Assert.Equal(first, input.Payloads[0].GetText());
    }

    [Theory]
    [InlineData("<!--outer--><r/>")]
    [InlineData("<r/><?outer value?>")]
    [InlineData("<!DOCTYPE r SYSTEM 'file:///never-read'><r/>")]
    [InlineData("<r>")]
    public async Task UnsupportedOrUnsafeDocumentsFailRatherThanDiscardingContent(string xml)
        => await Assert.ThrowsAsync<ArgumentException>(() => new XmlPartsRenderModule().RenderAsync(Context, Input(xml), Step("part"), default));

    [Theory]
    [InlineData("rootName", "p:Root")]
    [InlineData("partNames", "a,")]
    [InlineData("partNames", "p:a,b")]
    [InlineData("partNames", "a")]
    [InlineData("rootNamespace", "http://www.w3.org/2000/xmlns/")]
    public async Task InvalidNamesNamespacesAndInputArityFailExplicitly(string key, string value)
    {
        var step = Step(); step.Settings[key] = value;
        await Assert.ThrowsAsync<ArgumentException>(() => new XmlPartsRenderModule().RenderAsync(Context, Input("<r/>", "<s/>"), step, default));
    }

    [Fact]
    public async Task ArgumentAndNameBoundsAreExactAndEmptyBatchesAreNotSuccessfulAggregates()
    {
        var module = new XmlPartsRenderModule(); var step = Step(string.Join(',', Enumerable.Repeat(new string('p', 256), 32)));
        step.Settings["rootName"] = new string('r', 256);
        Assert.Single((await module.RenderAsync(Context, Input(Enumerable.Repeat("<r/>", 32).ToArray()), step, default)).Payloads);
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context, IntegrationBatch.Empty, step, default));
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context, Input(Enumerable.Repeat("<r/>", 33).ToArray()), step, default));
        step = Step("part"); step.Settings["rootName"] = new string('r', 257);
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context, Input("<r/>"), step, default));
    }

    [Fact]
    public async Task CombinedOutputSizeDepthAndCancellationAreValidated()
    {
        var module = new XmlPartsRenderModule();
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context, Input("<r>" + new string('x', 500000) + "</r>", "<s>" + new string('y', 500000) + "</s>"), Step(), default));
        var nearLimit = "<r>" + new string('x', XmlMappingSchemas.MaximumCharacters - 7) + "</r>";
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context, Input(nearLimit), Step("part"), default));
        var deep = string.Concat(Enumerable.Repeat("<r>", 64)) + string.Concat(Enumerable.Repeat("</r>", 64));
        await Assert.ThrowsAsync<ArgumentException>(() => module.RenderAsync(Context, Input(deep), Step("part"), default));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => module.RenderAsync(Context, Input("<r/>"), Step("part"), new CancellationToken(true)));
    }

    [Fact]
    public async Task OrderedPartsAndVisualXmlMapperRunAsARealRendererPipeline()
    {
        var catalog = new FakeExecutableModuleCatalog().AddRender(new XmlPartsRenderModule()).AddRender(new XmlMappingRenderModule());
        using var services = new ServiceCollection().AddSingleton<IModuleCatalog>(catalog).AddSingleton<IRenderModuleInvoker, Service.Modules.RenderModuleInvoker>().BuildServiceProvider();
        var module = new RenderPipelineModule(services.GetRequiredService<IRenderModuleInvoker>());
        var mapping = new XmlMappingDefinition { TargetRoot = "Result", Namespaces = new() { ["a"] = "http://schemas.microsoft.com/BizTalk/2003/aggschema" }, Fields = [
            new() { SourcePath = "/a:Root/InputMessagePart_0/Billing/Id", TargetPath = "Id" },
            new() { SourcePath = "/a:Root/InputMessagePart_1/MessageIndexer/Count", TargetPath = "Count" }
        ] };
        var step = new ModuleStepDefinition { Module = "render-pipeline", Settings = new() { ["stagesJson"] = JsonSerializer.Serialize(new[] {
            new { module = "xml-parts-render", settings = Step().Settings },
            new { module = "xml-mapping-render", settings = new Dictionary<string, string> { ["mappingJson"] = XmlMappingTests.Json(mapping) } }
        }, JsonSerializerOptions.Web) } };
        var output = Assert.Single((await module.RenderAsync(Context, Input("<Billing><Id>42</Id></Billing>", "<MessageIndexer><Count>2</Count></MessageIndexer>"), step, default)).Payloads);
        var root = XDocument.Parse(output.GetText()).Root!; Assert.Equal("42", root.Element("Id")!.Value); Assert.Equal("2", root.Element("Count")!.Value);
        Assert.False(output.Metadata.ContainsKey("private"));
    }

    [Fact]
    public async Task BuiltInRegistrationAndDescriptorExposeAllComposerSettingsInNativeUi()
    {
        var registry = new ModuleRegistryBuilder(); new BuiltInModuleInstaller().Install(registry);
        Assert.Contains(registry.Registrations, registration => registration.Kind == ModuleKind.Render && registration.ImplementationType == typeof(XmlPartsRenderModule));
        var descriptor = new XmlPartsRenderModule().Descriptor;
        var module = new ModuleViewModel(descriptor.Id, descriptor.DisplayName, "Render", descriptor.Description,
            descriptor.Settings.Select(setting => new ModuleSettingViewModel(setting.Key, setting.Label, setting.Description, setting.IsRequired, setting.InputKind.ToString(), setting.DefaultValue, [])).ToArray(), [], [], []);
        var model = new NativeFlowEditorModel { Durable = true, Nodes = [new() { Kind = "Render", Module = descriptor.Id, InputMessages = ["billing", "index"], MessageName = "parts", Settings = Step().Settings }] };
        await using var services = new ServiceCollection().AddLogging().AddSingleton<Microsoft.JSInterop.IJSRuntime, NoJs>().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var view = await renderer.RenderComponentAsync<NativeFlowEditor>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = JsonSerializer.Serialize(model, JsonSerializerOptions.Web), ["Modules"] = new[] { module } }));
            foreach (var setting in descriptor.Settings) Assert.Contains(setting.Label, view.ToHtmlString());
            Assert.Contains("Compose ordered XML input parts", view.ToHtmlString()); Assert.DoesNotContain("Some nodes are incomplete", view.ToHtmlString());
        });
    }

    private sealed class NoJs : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args) => throw new NotSupportedException();
    }
}
