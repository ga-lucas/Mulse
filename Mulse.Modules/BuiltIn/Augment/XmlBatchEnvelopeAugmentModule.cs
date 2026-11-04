using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace Mulse.Modules.BuiltIn.Augment;

public sealed class XmlBatchEnvelopeAugmentModule : IOrchestrationAugmentModule
{
    public ModuleDescriptor Descriptor { get; } = new("xml-batch-envelope-augment", "Wrap XML batch with message count", ModuleKind.OrchestrationAugment,
        "Combines the current XML payloads into one ordered envelope with MessageCount and Messages/Message elements for a subsequent visual map or XSLT header/body/trailer map. Counts payloads, not records inside them; shared metadata only is retained. No buffering or automatic legacy map translation.",
        [
            new("rootName", "Envelope root", "XML local name for the envelope root. MessageCount, Messages and Message share its namespace.", false, ModuleSettingInputKind.Text, "Batch"),
            new("namespace", "Envelope namespace", "Namespace URI for envelope elements; blank produces unqualified elements. Original message namespaces are preserved.", false, ModuleSettingInputKind.Text, "urn:mulse:batch"),
            new("outputFileName", "Output filename", "A single output filename, without directories.", false, ModuleSettingInputKind.Text, "batch.xml")
        ], new ModuleRecommendationProfile([ModuleDataFormat.Xml], capabilities: [ModuleCapability.Transformation]));

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rootName = ModuleSettingReader.GetOptional(step.Settings, "rootName") ?? "Batch";
        var nsText = step.Settings.GetValueOrDefault("namespace", "urn:mulse:batch");
        var fileName = ModuleSettingReader.GetOptional(step.Settings, "outputFileName") ?? "batch.xml";
        if (rootName.Length > 256 || nsText.Length > 2048) throw new ArgumentException("XML batch root/namespace exceeds 256/2048 characters.");
        XmlConvert.VerifyNCName(rootName);
        if (fileName.Length > 256 || fileName is "." or ".." || fileName != Path.GetFileName(fileName) || fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("XML batch outputFileName must be a single filename of at most 256 characters.");
        if (batch.Count > 10000 || batch.Payloads.Sum(p => (long)p.Content.ToMemory().Length) > 16000000)
            throw new ArgumentException("XML batch envelope accepts at most 10000 payloads / 16000000 content bytes.");
        if (batch.Count == 0) return Task.FromResult(IntegrationBatch.Empty);
        XNamespace ns = nsText;
        var messages = new XElement(ns + "Messages");
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = payload.Content.ToStream();
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16000000
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (document.Root is null) throw new ArgumentException("Every XML batch payload requires a document element.");
            messages.Add(new XElement(ns + "Message", document.Root));
        }
        var count = batch.Count.ToString(CultureInfo.InvariantCulture);
        var output = new XElement(ns + rootName, new XElement(ns + "MessageCount", count), messages).ToString(SaveOptions.DisableFormatting);
        if (output.Length > 24000000) throw new ArgumentException("XML batch envelope exceeds 24000000 characters.");
        var metadata = batch.Payloads[0].Metadata.Where(entry => batch.Payloads.All(p => p.Metadata.TryGetValue(entry.Key, out var value) && value == entry.Value))
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        metadata["batch.count"] = count;
        metadata["augmentedBy"] = Descriptor.Id;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new IntegrationBatch([new(fileName, BinaryData.FromString(output), "application/xml", metadata)]));
    }
}
