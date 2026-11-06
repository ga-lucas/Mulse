using System.Xml;
using System.Xml.Linq;

namespace Mulse.Hl7;

/// <summary>
/// Native equivalent of an <c>HL7TypeCaster</c> send-pipeline component, which promoted the BizTalk
/// system <c>MessageType</c> as <c>http://microsoft.com/HealthCare/HL7/2X#&lt;root&gt;</c> so the HL7 assembler could
/// pick the schema. Here the promotion is an explicit metadata value; the body is never rewritten.
/// </summary>
public sealed class Hl7MessageTypeAugmentModule : IOrchestrationAugmentModule
{
    private const string DefaultKey = "{http://schemas.microsoft.com/BizTalk/2003/system-properties}MessageType";

    public ModuleDescriptor Descriptor { get; } = new("hl7-message-type-augment", "Promote HL7 message type", ModuleKind.OrchestrationAugment,
        "Reads the XML root element of each HL7 XML payload and writes '<namespace>#<root>' into an explicit metadata key, replacing an HL7TypeCaster pipeline component. The body is unchanged; fails if a payload is not well-formed XML.",
        [
            new("namespace", "Message type namespace", "Namespace prepended to the root element name. The source component used the Microsoft HL7 2.X namespace.", false, ModuleSettingInputKind.Text, "http://microsoft.com/HealthCare/HL7/2X"),
            new("outputKey", "Output metadata key", "Metadata key that receives the message type. Defaults to the BizTalk system MessageType property identity.", false, ModuleSettingInputKind.Text, DefaultKey),
            new("overwriteExisting", "Overwrite existing", "Replace an existing value at the output key.", false, ModuleSettingInputKind.Boolean, "true")
        ]);

    public Task<IntegrationBatch> AugmentAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var ns = ModuleSettingReader.GetOptional(step.Settings, "namespace") ?? "http://microsoft.com/HealthCare/HL7/2X";
        var outputKey = ModuleSettingReader.GetOptional(step.Settings, "outputKey") ?? DefaultKey;
        var overwrite = !string.Equals(ModuleSettingReader.GetOptional(step.Settings, "overwriteExisting"), "false", StringComparison.OrdinalIgnoreCase);
        if (outputKey.Length is 0 or > 256)
        {
            throw new ArgumentException("HL7 message type requires an output metadata key of at most 256 characters.");
        }

        var outputs = new List<IntegrationPayload>(batch.Count);
        foreach (var payload in batch.Payloads)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string root;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var reader = XmlReader.Create(new StringReader(payload.Content.ToString()), settings);
                root = XDocument.Load(reader).Root?.Name.LocalName ?? string.Empty;
            }
            catch (XmlException exception)
            {
                throw new ArgumentException($"HL7 message type could not read '{payload.Name}' as XML.", exception);
            }

            if (root.Length == 0)
            {
                throw new ArgumentException($"HL7 message type found no root element in '{payload.Name}'.");
            }

            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.Ordinal);
            if (overwrite || !metadata.ContainsKey(outputKey))
            {
                metadata[outputKey] = $"{ns}#{root}";
            }

            outputs.Add(payload with { Metadata = metadata });
        }

        return Task.FromResult(new IntegrationBatch(outputs));
    }
}
