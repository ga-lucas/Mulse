using System.Xml.Linq;
using System.Xml.Schema;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class XmlXsdParseModule : IParseModule
{
    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("schemaPaths", "Schema paths", "Optional newline or semicolon separated XSD file paths used to validate incoming XML payloads.", false, ModuleSettingInputKind.TextArea),
        new("expectedRootElement", "Expected root element", "Optional expected XML root element local name.", false),
        new("preserveWhitespace", "Preserve whitespace", "Preserves incoming XML whitespace when enabled.", false, ModuleSettingInputKind.Boolean, "true")
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Xml],
        capabilities: [ModuleCapability.Parsing, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        "xml-xsd-parse",
        "XML/XSD-aware parse",
        ModuleKind.Parse,
        "Parses XML payloads, optionally validates them against one or more XSD schemas, and preserves XML as the working document for downstream compatibility modules.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> ParseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preserveWhitespace = ModuleSettings.GetBoolean(step.Settings, "preserveWhitespace", defaultValue: true, Descriptor.Id);
        var expectedRootElement = ModuleSettings.GetOptional(step.Settings, "expectedRootElement");
        var schemaPaths = ResolveSchemaPaths(ModuleSettings.GetOptional(step.Settings, "schemaPaths"));
        var schemas = schemaPaths.Count == 0 ? null : CreateSchemaSet(schemaPaths);

        var parsedPayloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var document = CompatibilityPayloadNavigator.ParseXml(payload, Descriptor.Id, preserveWhitespace);
            ValidateRootElement(document, payload.Name, expectedRootElement);
            ValidateDocument(document, payload.Name, schemas);

            var xml = preserveWhitespace
                ? document.ToString(SaveOptions.DisableFormatting)
                : document.ToString();
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["parsedBy"] = Descriptor.Id,
                ["parsedFormat"] = "Xml",
                ["xmlRootElement"] = document.Root?.Name.LocalName ?? string.Empty,
                ["xmlNamespace"] = document.Root?.Name.NamespaceName ?? string.Empty,
                ["schemaValidated"] = (schemas is not null).ToString()
            };

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".xml"),
                BinaryData.FromString(xml),
                "application/xml",
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(parsedPayloads));
    }

    private static IReadOnlyList<string> ResolveSchemaPaths(string? schemaPaths)
    {
        if (string.IsNullOrWhiteSpace(schemaPaths))
        {
            return [];
        }

        return schemaPaths
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static XmlSchemaSet CreateSchemaSet(IReadOnlyList<string> schemaPaths)
    {
        var schemaSet = new XmlSchemaSet();
        foreach (var schemaPath in schemaPaths)
        {
            if (!File.Exists(schemaPath))
            {
                throw new FileNotFoundException($"Schema file '{schemaPath}' was not found.", schemaPath);
            }

            schemaSet.Add(null, schemaPath);
        }

        schemaSet.Compile();
        return schemaSet;
    }

    private static void ValidateRootElement(XDocument document, string payloadName, string? expectedRootElement)
    {
        if (string.IsNullOrWhiteSpace(expectedRootElement))
        {
            return;
        }

        var actualRoot = document.Root?.Name.LocalName;
        if (!string.Equals(actualRoot, expectedRootElement, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"XML payload '{payloadName}' root element '{actualRoot}' did not match expected root '{expectedRootElement}'.");
        }
    }

    private static void ValidateDocument(XDocument document, string payloadName, XmlSchemaSet? schemaSet)
    {
        if (schemaSet is null)
        {
            return;
        }

        var validationErrors = new List<string>();
        document.Validate(schemaSet, (_, args) => validationErrors.Add(args.Message), addSchemaInfo: true);
        if (validationErrors.Count > 0)
        {
            throw new InvalidOperationException($"XML payload '{payloadName}' failed schema validation: {string.Join(" | ", validationErrors)}");
        }
    }
}
