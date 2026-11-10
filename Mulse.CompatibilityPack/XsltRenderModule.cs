using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Xsl;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class XsltRenderModule : IRenderModule
{
    public const int MaxStylesheetCharacters = 1_048_576;
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("xsltPath", "XSLT path", "The path to the XSLT stylesheet used to transform the XML payload. Leave empty when the stylesheet is entered inline below; exactly one of the two is required.", false),
        new("xsltText", "XSLT text", "The XSLT 1.0 stylesheet itself, editable here instead of a file path (for example a BizTalk custom-XSLT map). Script blocks (msxsl:script) and document() are not run; replace them with XPath or other stylesheet logic.", false, ModuleSettingInputKind.TextArea),
        new("parametersJson", "Parameters JSON", "An optional JSON object of XSLT parameters.", false, ModuleSettingInputKind.TextArea, "{}"),
        new("outputExtension", "Output extension", "The file extension for transformed payloads.", false, ModuleSettingInputKind.Text, ".xml")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Xml],
        capabilities: [ModuleCapability.Mapping, ModuleCapability.Serialization, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "xslt-render",
        "XSLT render",
        ModuleKind.Render,
        "Transforms XML payloads with XSLT so BizTalk-style map assets can be migrated into the render stage.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var xsltPathSetting = ModuleSettings.GetOptional(step.Settings, "xsltPath");
        var xsltText = ModuleSettings.GetOptional(step.Settings, "xsltText");
        if (string.IsNullOrWhiteSpace(xsltPathSetting) == string.IsNullOrWhiteSpace(xsltText))
        {
            throw new InvalidOperationException($"Module '{Descriptor.Id}' requires exactly one of 'xsltPath' or 'xsltText'.");
        }

        var parameters = CompatibilityPayloadNavigator.ParseScalarObject(ModuleSettings.GetOptional(step.Settings, "parametersJson"), Descriptor.Id, "parametersJson");
        var outputExtension = ModuleSettings.GetOptional(step.Settings, "outputExtension") ?? ".xml";

        var transform = new XslCompiledTransform();
        var readerSettings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
        if (!string.IsNullOrWhiteSpace(xsltPathSetting))
        {
            var xsltPath = Path.GetFullPath(xsltPathSetting);
            if (!File.Exists(xsltPath))
            {
                throw new FileNotFoundException($"XSLT file '{xsltPath}' was not found.", xsltPath);
            }

            using var stylesheetReader = XmlReader.Create(xsltPath, readerSettings);
            LoadStylesheet(transform, stylesheetReader);
        }
        else
        {
            if (xsltText!.Length > MaxStylesheetCharacters)
            {
                throw new InvalidOperationException($"Inline XSLT must not exceed {MaxStylesheetCharacters} characters.");
            }

            using var stylesheetReader = XmlReader.Create(new StringReader(xsltText), readerSettings);
            LoadStylesheet(transform, stylesheetReader);
        }

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = CompatibilityPayloadNavigator.ParseXml(payload, Descriptor.Id);
            var transformed = Transform(transform, document, parameters);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id,
                ["renderedFormat"] = "Xslt"
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, outputExtension),
                BinaryData.FromString(transformed),
                string.Equals(outputExtension, ".xml", StringComparison.OrdinalIgnoreCase) ? "application/xml" : "text/plain",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(payloads));
    }

    private static readonly System.Xml.Linq.XName MsxslScript = System.Xml.Linq.XNamespace.Get("urn:schemas-microsoft-com:xslt") + "script";
    private const string ScriptsBlocked = "The stylesheet contains msxsl:script blocks, which cannot run here. Rewrite each script function the stylesheet calls (for example userCSharp:Name(...)) as XPath or stylesheet logic, then remove the msxsl:script blocks from the XSLT.";

    private static void LoadStylesheet(XslCompiledTransform transform, XmlReader reader)
    {
        var stylesheet = System.Xml.Linq.XDocument.Load(reader, System.Xml.Linq.LoadOptions.PreserveWhitespace);
        if (stylesheet.Descendants(MsxslScript).Any())
        {
            throw new InvalidOperationException(ScriptsBlocked);
        }

        using var checkedReader = stylesheet.CreateReader();
        transform.Load(checkedReader, XsltSettings.Default, stylesheetResolver: null);
    }

    private static string Transform(XslCompiledTransform transform, System.Xml.Linq.XDocument document, IReadOnlyDictionary<string, string> parameters)
    {
        var arguments = new XsltArgumentList();
        foreach (var parameter in parameters)
        {
            arguments.AddParam(parameter.Key, string.Empty, parameter.Value);
        }

        using var stringWriter = new StringWriter();
        using var writer = XmlWriter.Create(stringWriter, transform.OutputSettings);
        using var reader = document.CreateReader();
        transform.Transform(reader, arguments, writer);
        writer.Flush();
        return stringWriter.ToString();
    }
}
