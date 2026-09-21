using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

public sealed class SoapEnvelopeRenderModule : IRenderModule
{
    private const string ModuleId = "soap-envelope-render";

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("soapVersion", "SOAP version", "The SOAP envelope version to emit.", false, ModuleSettingInputKind.Select, "1.2",
        [
            new ModuleSettingOption("1.1", "SOAP 1.1"),
            new ModuleSettingOption("1.2", "SOAP 1.2")
        ]),
        new("actionMappingsJson", "Action mappings JSON", "Optional JSON object mapping operation or root element names to SOAP action URIs.", false, ModuleSettingInputKind.TextArea, "{}"),
        new("fallbackAction", "Fallback action", "Optional SOAP action to use when the payload root element does not match an action mapping.", false)
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Xml],
        [ModuleProtocol.Http],
        [ModuleCapability.Serialization, ModuleCapability.Transformation]);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleId,
        "SOAP envelope render",
        ModuleKind.Render,
        "Wraps XML payloads in a SOAP envelope and emits SOAP-specific HTTP metadata for downstream delivery modules.",
        SettingDescriptors,
        Recommendation);

    public Task<IntegrationBatch> RenderAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var soapVersion = ModuleSettings.GetOptional(step.Settings, "soapVersion") ?? "1.2";
        var fallbackAction = ModuleSettings.GetOptional(step.Settings, "fallbackAction");
        var actionMappings = ParseActionMappings(ModuleSettings.GetOptional(step.Settings, "actionMappingsJson"));
        var settings = ResolveSoapSettings(soapVersion);

        var renderedPayloads = batch.Payloads.Select(payload =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceXml = payload.Content.ToString();
            var document = XDocument.Parse(sourceXml, LoadOptions.PreserveWhitespace);
            var operationName = document.Root?.Name.LocalName ?? string.Empty;
            var action = ResolveAction(actionMappings, operationName, fallbackAction);
            var envelope = CreateEnvelope(document.Root, settings.EnvelopeNamespace);
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                ["renderedBy"] = Descriptor.Id,
                ["renderedFormat"] = "Soap",
                ["soapVersion"] = soapVersion,
                ["contentTypeOverride"] = CreateContentType(settings, action)
            };

            if (string.Equals(soapVersion, "1.1", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(action))
            {
                metadata["httpHeader.SOAPAction"] = action;
            }

            if (!string.IsNullOrWhiteSpace(action))
            {
                metadata["soapAction"] = action;
            }

            return new IntegrationPayload(
                Path.ChangeExtension(payload.Name, ".xml"),
                BinaryData.FromString(envelope.ToString(SaveOptions.DisableFormatting)),
                settings.DefaultContentType,
                metadata);
        }).ToArray();

        return Task.FromResult(new IntegrationBatch(renderedPayloads));
    }

    private static IReadOnlyDictionary<string, string> ParseActionMappings(string? actionMappingsJson)
    {
        if (string.IsNullOrWhiteSpace(actionMappingsJson))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var jsonObject = JsonNode.Parse(actionMappingsJson) as JsonObject;
        if (jsonObject is null)
        {
            throw new ArgumentException($"Module '{ModuleId}' requires actionMappingsJson to be a JSON object.", nameof(actionMappingsJson));
        }

        return jsonObject.ToDictionary(
            static entry => entry.Key,
            static entry => ExtractScalarText(entry.Value) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    }

    private static string? ExtractScalarText(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            return value.TryGetValue<string>(out var stringValue)
                ? stringValue
                : value.ToJsonString().Trim('"');
        }

        return node.ToJsonString();
    }

    private static SoapSettings ResolveSoapSettings(string soapVersion)
    {
        return soapVersion switch
        {
            "1.1" => new SoapSettings("http://schemas.xmlsoap.org/soap/envelope/", "text/xml; charset=utf-8"),
            "1.2" => new SoapSettings("http://www.w3.org/2003/05/soap-envelope", "application/soap+xml; charset=utf-8"),
            _ => throw new ArgumentException($"Module '{ModuleId}' only supports SOAP versions 1.1 and 1.2.", nameof(soapVersion))
        };
    }

    private static string? ResolveAction(IReadOnlyDictionary<string, string> actionMappings, string operationName, string? fallbackAction)
    {
        if (!string.IsNullOrWhiteSpace(operationName)
            && actionMappings.TryGetValue(operationName, out var action)
            && !string.IsNullOrWhiteSpace(action))
        {
            return action;
        }

        return string.IsNullOrWhiteSpace(fallbackAction) ? null : fallbackAction;
    }

    private static XDocument CreateEnvelope(XElement? payloadRoot, XNamespace envelopeNamespace)
    {
        return new XDocument(
            new XElement(envelopeNamespace + "Envelope",
                new XElement(envelopeNamespace + "Body", payloadRoot is null ? null : new XElement(payloadRoot))));
    }

    private static string CreateContentType(SoapSettings settings, string? action)
    {
        if (string.IsNullOrWhiteSpace(action) || settings.DefaultContentType.StartsWith("text/xml", StringComparison.OrdinalIgnoreCase))
        {
            return settings.DefaultContentType;
        }

        return $"{settings.DefaultContentType}; action=\"{action}\"";
    }

    private sealed record SoapSettings(XNamespace EnvelopeNamespace, string DefaultContentType);
}
