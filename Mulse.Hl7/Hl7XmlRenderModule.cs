using System.Text;
using System.Xml;
using System.Xml.Linq;
using Mulse.Modules;

namespace Mulse.Hl7;

/// <summary>
/// Renders HL7 v2 XML (the BizTalk BTAHL7 / HL7 v2 XML encoding: segment elements such as <c>MSH</c> containing field
/// elements <c>MSH.9</c>, component elements <c>MSH.9.1</c> and subcomponent elements <c>MSH.9.1.1</c>) to ER7 text.
/// Segment-group wrapper elements are flattened in document order. Escape-sequence encoding is not applied.
/// </summary>
public sealed class Hl7XmlRenderModule : IRenderModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("trimTrailingDelimiters", "Trim trailing delimiters", "Removes empty trailing fields from each segment. Imported from the BTAHL7 assembler 'TrailingDelimiterAllowed' property (allowed = off).", false, ModuleSettingInputKind.Boolean, "false"),
        new("segmentTerminator", "Segment terminator", "Characters ending each segment. CR is the HL7 standard.", false, ModuleSettingInputKind.Select, "CR",
            [new("CR", "CR (HL7 standard)"), new("LF", "LF"), new("CRLF", "CRLF")])
    ];

    public ModuleDescriptor Descriptor { get; } = new(
        "hl7-xml-render",
        "HL7 XML to ER7 render",
        ModuleKind.Render,
        "Renders HL7 v2 XML (BizTalk BTAHL7 encoding) into pipe-delimited HL7 ER7 text, replacing the BTAHL7 assembler for the common case.",
        SettingDescriptors,
        new ModuleRecommendationProfile([ModuleDataFormat.Xml], capabilities: [ModuleCapability.Serialization, ModuleCapability.Transformation]));

    public Task<IntegrationBatch> RenderAsync(FlowExecutionContext context, IntegrationBatch batch, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        var trim = step.Settings.TryGetValue("trimTrailingDelimiters", out var trimText) && bool.TryParse(trimText, out var trimValue) && trimValue;
        var terminatorSetting = step.Settings.TryGetValue("segmentTerminator", out var terminatorText) && !string.IsNullOrWhiteSpace(terminatorText) ? terminatorText.Trim() : "CR";
        var terminator = terminatorSetting.ToUpperInvariant() switch
        {
            "CR" => "\r",
            "LF" => "\n",
            "CRLF" => "\r\n",
            var other => throw new ArgumentException($"Unsupported segment terminator '{other}'; use CR, LF or CRLF.")
        };

        var payloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rendered = Render(payload.Name, payload.Content.ToString(), trim, terminator);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id,
                ["renderedFormat"] = "Hl7"
            };
            return new IntegrationPayload(Path.ChangeExtension(payload.Name, ".hl7"), BinaryData.FromString(rendered), "text/hl7-v2", metadata);
        }).ToArray();
        return Task.FromResult(new IntegrationBatch(payloads));
    }

    internal static string Render(string name, string xml, bool trim, string terminator)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (XmlException exception)
        {
            throw new InvalidOperationException($"HL7 XML render could not read '{name}' as XML.", exception);
        }

        var segments = document.Root!.DescendantsAndSelf().Where(IsSegment).ToArray();
        if (segments.Length == 0 || segments[0].Name.LocalName != "MSH")
        {
            throw new InvalidOperationException($"HL7 XML render found no leading MSH segment in '{name}'.");
        }

        var msh1 = segments[0].Elements().FirstOrDefault(static e => e.Name.LocalName == "MSH.1")?.Value;
        var msh2 = segments[0].Elements().FirstOrDefault(static e => e.Name.LocalName == "MSH.2")?.Value;
        var field = string.IsNullOrEmpty(msh1) ? "|" : msh1[..1];
        var encoding = string.IsNullOrEmpty(msh2) ? "^~\\&" : msh2;
        if (encoding.Length < 4)
        {
            throw new InvalidOperationException($"HL7 XML render needs four MSH.2 encoding characters in '{name}'.");
        }

        char component = encoding[0], repeat = encoding[1], subcomponent = encoding[3];
        var builder = new StringBuilder();
        foreach (var segment in segments)
        {
            var segmentName = segment.Name.LocalName;
            var first = segmentName == "MSH" ? 2 : 1;
            var values = new SortedDictionary<int, List<string>>();
            foreach (var fieldElement in segment.Elements())
            {
                var parts = fieldElement.Name.LocalName.Split('.');
                if (parts.Length != 2 || parts[0] != segmentName || !int.TryParse(parts[1], out var index) || index < first)
                {
                    continue;
                }

                if (!values.TryGetValue(index, out var list))
                {
                    values[index] = list = [];
                }

                list.Add(RenderField(fieldElement, component, subcomponent));
            }

            var count = values.Count == 0 ? 0 : values.Keys.Max() - first + 1;
            var fields = new string[count];
            for (var i = 0; i < count; i++)
            {
                fields[i] = values.TryGetValue(i + first, out var list) ? string.Join(repeat, list) : string.Empty;
            }

            var end = fields.Length;
            if (trim)
            {
                while (end > 0 && fields[end - 1].Length == 0)
                {
                    end--;
                }
            }

            builder.Append(segmentName);
            if (segmentName == "MSH")
            {
                builder.Append(field);
            }

            for (var i = 0; i < end; i++)
            {
                if (i > 0 || segmentName != "MSH")
                {
                    builder.Append(field);
                }

                builder.Append(fields[i]);
            }

            builder.Append(terminator);
        }

        return builder.ToString();
    }

    private static bool IsSegment(XElement element)
    {
        var name = element.Name.LocalName;
        return name.Length == 3 && name.All(static c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c))
            && element.Elements().Any(child => child.Name.LocalName.StartsWith(name + ".", StringComparison.Ordinal));
    }

    private static string RenderField(XElement fieldElement, char component, char subcomponent)
    {
        var prefix = fieldElement.Name.LocalName + ".";
        var components = fieldElement.Elements().Where(e => e.Name.LocalName.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
        if (components.Length == 0)
        {
            return fieldElement.Value;
        }

        var byIndex = new SortedDictionary<int, string>();
        foreach (var part in components)
        {
            if (!int.TryParse(part.Name.LocalName[prefix.Length..].Split('.')[0], out var index))
            {
                continue;
            }

            var subPrefix = part.Name.LocalName + ".";
            var subs = part.Elements().Where(e => e.Name.LocalName.StartsWith(subPrefix, StringComparison.Ordinal)).ToArray();
            byIndex[index] = subs.Length == 0 ? part.Value : string.Join(subcomponent, subs.Select(static s => s.Value));
        }

        var max = byIndex.Count == 0 ? 0 : byIndex.Keys.Max();
        return string.Join(component, Enumerable.Range(1, max).Select(i => byIndex.GetValueOrDefault(i, string.Empty)));
    }
}
