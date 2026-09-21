using System.Xml.Linq;
using System.Xml.XPath;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

/// <summary>
/// Splits one incoming XML envelope document into multiple business-message payloads, mirroring
/// BizTalk's envelope debatching (Microsoft.BizTalk.Component.XmlDasmComp with EnvelopeSpecNames):
/// a single inbound file containing a repeated "body" element becomes one payload per repetition,
/// each carried downstream independently through parse/augment/deliver.
/// </summary>
public sealed class XmlEnvelopeDebatchParseModule : IParseModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("bodyXPath", "Body XPath", "An XPath expression (e.g. //*[local-name()='Body']) selecting each repeated business-message element to split out of the envelope.", true),
        new("preserveEnvelopeMetadata", "Preserve envelope metadata", "Copies the envelope root element's attributes onto each split message as 'envelope.<name>' metadata, approximating BizTalk envelope property promotion.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Xml],
        capabilities: [ModuleCapability.Parsing, ModuleCapability.Envelope]);

    public ModuleDescriptor Descriptor { get; } = new(
        "xml-envelope-debatch-parse",
        "XML envelope debatch parse",
        ModuleKind.Parse,
        "Splits one XML envelope document into multiple business-message payloads using an XPath selector, for migrating BizTalk envelope/batch receive pipelines.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> ParseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bodyXPath = ModuleSettings.GetRequired(step.Settings, "bodyXPath", Descriptor.Id);
        var preserveEnvelopeMetadata = ModuleSettings.GetBoolean(step.Settings, "preserveEnvelopeMetadata", defaultValue: true, Descriptor.Id);

        var debatchedPayloads = new List<IntegrationPayload>(batch.Count);
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = CompatibilityPayloadNavigator.ParseXml(payload, Descriptor.Id, preserveWhitespace: true);
            var envelopeMetadata = preserveEnvelopeMetadata
                ? ExtractEnvelopeMetadata(document)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<XElement> bodyElements;
            try
            {
                bodyElements = document.XPathSelectElements(bodyXPath).ToArray();
            }
            catch (System.Xml.XPath.XPathException exception)
            {
                throw new ArgumentException($"Module '{Descriptor.Id}' has an invalid bodyXPath expression '{bodyXPath}'.", nameof(step), exception);
            }

            if (bodyElements.Count == 0)
            {
                throw new InvalidOperationException($"Module '{Descriptor.Id}' found no elements matching bodyXPath '{bodyXPath}' in envelope payload '{payload.Name}'. Confirm the XPath matches the real envelope shape before enabling this flow.");
            }

            for (var index = 0; index < bodyElements.Count; index++)
            {
                var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
                {
                    ["parsedBy"] = Descriptor.Id,
                    ["parsedFormat"] = "Xml",
                    ["debatchedFrom"] = payload.Name,
                    ["debatchIndex"] = index.ToString(),
                    ["debatchCount"] = bodyElements.Count.ToString()
                };

                foreach (var entry in envelopeMetadata)
                {
                    metadata[$"envelope.{entry.Key}"] = entry.Value;
                }

                debatchedPayloads.Add(new IntegrationPayload(
                    $"{Path.GetFileNameWithoutExtension(payload.Name)}-{index}.xml",
                    BinaryData.FromString(bodyElements[index].ToString(SaveOptions.DisableFormatting)),
                    "application/xml",
                    metadata));
            }
        }

        return Task.FromResult(new IntegrationBatch(debatchedPayloads));
    }

    private static IReadOnlyDictionary<string, string> ExtractEnvelopeMetadata(XDocument document)
    {
        if (document.Root is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["rootElement"] = document.Root.Name.LocalName
        };

        foreach (var attribute in document.Root.Attributes())
        {
            metadata[attribute.Name.LocalName] = attribute.Value;
        }

        return metadata;
    }
}
