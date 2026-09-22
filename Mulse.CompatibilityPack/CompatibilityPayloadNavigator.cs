using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Mulse.Modules;

namespace Mulse.CompatibilityPack;

internal static class CompatibilityPayloadNavigator
{
    public static JsonNode ParseJson(IntegrationPayload payload, string moduleId)
    {
        try
        {
            return JsonNode.Parse(payload.GetText())
                ?? throw new InvalidOperationException($"Module '{moduleId}' could not parse payload '{payload.Name}' as JSON.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Module '{moduleId}' requires JSON payloads. Payload '{payload.Name}' was not valid JSON.", exception);
        }
    }

    public static XDocument ParseXml(IntegrationPayload payload, string moduleId, bool preserveWhitespace = true)
    {
        try
        {
            return XDocument.Parse(payload.GetText(), preserveWhitespace ? LoadOptions.PreserveWhitespace : LoadOptions.None);
        }
        catch (Exception exception) when (exception is XmlException or InvalidOperationException)
        {
            throw new InvalidOperationException($"Module '{moduleId}' requires XML payloads. Payload '{payload.Name}' was not valid XML.", exception);
        }
    }

    public static IReadOnlyList<string> SelectValues(IntegrationPayload payload, string selector, string moduleId, string? namespacesJson = null)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return [];
        }

        return IsJsonSelector(selector)
            ? SelectJsonValues(payload, selector, moduleId)
            : SelectXmlValues(payload, selector, moduleId, namespacesJson);
    }

    public static IReadOnlyDictionary<string, string> ParseScalarObject(string? json, string moduleId, string settingName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        JsonObject node;
        try
        {
            node = JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new ArgumentException($"Module '{moduleId}' requires setting '{settingName}' to be a JSON object.", settingName, exception);
        }

        return node.ToDictionary(
            static entry => entry.Key,
            static entry => JsonPayloadNavigator.ExtractScalarText(entry.Value) ?? string.Empty,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SelectJsonValues(IntegrationPayload payload, string selector, string moduleId)
    {
        var root = ParseJson(payload, moduleId);
        return JsonPayloadNavigator.ReadAllNodes(root, selector)
            .Select(JsonPayloadNavigator.ExtractScalarText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    private static IReadOnlyList<string> SelectXmlValues(IntegrationPayload payload, string selector, string moduleId, string? namespacesJson)
    {
        var document = ParseXml(payload, moduleId);
        var namespaceManager = CreateNamespaceManager(document, namespacesJson, moduleId);
        object? evaluation;

        try
        {
            evaluation = namespaceManager is null
                ? document.XPathEvaluate(selector)
                : document.XPathEvaluate(selector, namespaceManager);
        }
        catch (XPathException exception)
        {
            throw new InvalidOperationException($"Module '{moduleId}' could not evaluate XPath selector '{selector}'.", exception);
        }

        return ConvertXPathResult(evaluation)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
    }

    private static XmlNamespaceManager? CreateNamespaceManager(XDocument document, string? namespacesJson, string moduleId)
    {
        var mappings = ParseScalarObject(namespacesJson, moduleId, "namespacesJson");
        if (mappings.Count == 0)
        {
            return null;
        }

        var manager = new XmlNamespaceManager(new NameTable());
        foreach (var mapping in mappings)
        {
            manager.AddNamespace(mapping.Key, mapping.Value);
        }

        if (document.Root is { Name.NamespaceName.Length: > 0 } root && string.IsNullOrWhiteSpace(manager.LookupPrefix(root.Name.NamespaceName)))
        {
            manager.AddNamespace("default", root.Name.NamespaceName);
        }

        return manager;
    }

    private static IReadOnlyList<string> ConvertXPathResult(object? evaluation)
    {
        if (evaluation is null)
        {
            return [];
        }

        if (evaluation is string stringValue)
        {
            return [stringValue];
        }

        if (evaluation is bool or double or float or decimal or int or long)
        {
            return [Convert.ToString(evaluation, CultureInfo.InvariantCulture) ?? string.Empty];
        }

        if (evaluation is IEnumerable enumerable)
        {
            var values = new List<string>();
            foreach (var item in enumerable)
            {
                switch (item)
                {
                    case null:
                        break;
                    case XElement element:
                        values.Add(element.Value);
                        break;
                    case XAttribute attribute:
                        values.Add(attribute.Value);
                        break;
                    case XPathNavigator navigator:
                        values.Add(navigator.Value);
                        break;
                    default:
                        values.Add(Convert.ToString(item, CultureInfo.InvariantCulture) ?? string.Empty);
                        break;
                }
            }

            return values;
        }

        return [Convert.ToString(evaluation, CultureInfo.InvariantCulture) ?? string.Empty];
    }

    private static bool IsJsonSelector(string selector)
        => selector.TrimStart().StartsWith('$');
}
