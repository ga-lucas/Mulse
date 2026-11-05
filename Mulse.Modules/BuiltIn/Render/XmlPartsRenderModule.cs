using System.Xml;
using System.Xml.Linq;
using Mulse.Modules.Mapping;

namespace Mulse.Modules.BuiltIn.Render;

public sealed class XmlPartsRenderModule : IRenderModule
{
    public ModuleDescriptor Descriptor { get; } = new("xml-parts-render", "Compose ordered XML input parts", ModuleKind.Render,
        "Wraps ordered XML inputs in explicitly named parts under one configured root. Use before a multi-input XML map in an ordered renderer pipeline. No BizTalk assembly or map is loaded, and no input metadata is implicitly merged.",
        [
            new("rootName", "Root local name", "Unprefixed XML local name for the aggregate root (1-256 characters).", true),
            new("rootNamespace", "Root namespace", "Explicit namespace URI for the aggregate root; blank means no namespace.", false),
            new("partNames", "Ordered part local names", "Comma-separated unprefixed local names, one per input in argument order (maximum 32). Part elements have no namespace. Repeated names are allowed when explicitly required by the target contract.", true),
            new("outputName", "Output payload name", "Explicit aggregate payload name. No input metadata is copied.", true, ModuleSettingInputKind.Text, "parts.xml")
        ]);

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rootName = ModuleSettingReader.GetRequired(step.Settings, "rootName", Descriptor.Id);
        var ns = ModuleSettingReader.GetOptional(step.Settings, "rootNamespace") ?? "";
        var names = ModuleSettingReader.GetRequired(step.Settings, "partNames", Descriptor.Id);
        var outputName = ModuleSettingReader.GetRequired(step.Settings, "outputName", Descriptor.Id);
        if (names.Length > 8223 || outputName.Length > 256 || ns.Length > 2048 || ns is "http://www.w3.org/2000/xmlns/" or "http://www.w3.org/XML/1998/namespace")
            throw new ArgumentException("XML parts require bounded local names, namespace (at most 2048 characters) and output name (at most 256 characters); reserved namespaces are unsupported.");
        var parts = names.Split(',').Select(name => name.Trim()).ToArray();
        if (batch.Count is < 1 or > 32 || parts.Length != batch.Count)
            throw new ArgumentException("XML parts require 1-32 inputs and exactly one configured part name for each input.");
        ValidateName(rootName);
        foreach (var name in parts) ValidateName(name);
        var root = new XElement(XName.Get(rootName, ns));
        var characters = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = batch.Payloads[index].GetText();
            if (text.Length > XmlMappingSchemas.MaximumCharacters - characters)
                throw new ArgumentException("XML parts input documents together exceed 1,000,000 characters.");
            characters += text.Length;
            var document = XmlMappingSchemas.ReadXml(text, preserveWhitespace: true);
            if (document.Nodes().Any(node => node is not XElement && (node is not XText whitespace || !string.IsNullOrWhiteSpace(whitespace.Value))))
                throw new ArgumentException("XML parts cannot discard document-level comments or processing instructions; provide a root-only document with an optional XML declaration and whitespace.");
            root.Add(new XElement(parts[index], new XElement(document.Root!)));
        }
        var output = root.ToString(SaveOptions.DisableFormatting);
        _ = XmlMappingSchemas.ReadXml(output, preserveWhitespace: true);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new IntegrationBatch([new(outputName, BinaryData.FromString(output), "application/xml", new Dictionary<string, string>(StringComparer.Ordinal))]));
    }

    private static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 256) throw new ArgumentException("XML part/root local names require 1-256 characters.");
        try { XmlConvert.VerifyNCName(name); }
        catch (XmlException exception) { throw new ArgumentException("XML part/root names must be unprefixed XML local names.", exception); }
    }
}
