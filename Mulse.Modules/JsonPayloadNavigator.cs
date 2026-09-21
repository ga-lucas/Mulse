using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mulse.Modules;

internal static class JsonPayloadNavigator
{
    public static JsonNode Parse(BinaryData content, string moduleId, string payloadName)
    {
        try
        {
            return JsonNode.Parse(content.ToString())
                ?? throw new InvalidOperationException($"Module '{moduleId}' could not parse payload '{payloadName}' as JSON.");
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Module '{moduleId}' requires JSON payloads. Payload '{payloadName}' was not valid JSON.", exception);
        }
    }

    public static IReadOnlyList<string> ReadStringValues(JsonNode? node, string path)
    {
        return SelectNodes(node, path)
            .Select(ExtractScalarText)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Cast<string>()
            .ToArray();
    }

    public static JsonNode? ReadFirstNode(JsonNode? node, string path)
    {
        return SelectNodes(node, path)
            .FirstOrDefault(static candidate => candidate is not null)
            ?.DeepClone();
    }

    public static string? ExtractScalarText(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var stringValue))
            {
                return stringValue;
            }

            if (value.TryGetValue<bool>(out var booleanValue))
            {
                return booleanValue.ToString();
            }

            if (value.TryGetValue<long>(out var integerValue))
            {
                return integerValue.ToString();
            }

            if (value.TryGetValue<decimal>(out var decimalValue))
            {
                return decimalValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<double>(out var doubleValue))
            {
                return doubleValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.TryGetValue<DateTimeOffset>(out var dateTimeOffsetValue))
            {
                return dateTimeOffsetValue.ToString("O");
            }

            if (value.TryGetValue<DateTime>(out var dateTimeValue))
            {
                return dateTimeValue.ToString("O");
            }

            if (value.TryGetValue<Guid>(out var guidValue))
            {
                return guidValue.ToString();
            }
        }

        return node.ToJsonString();
    }

    public static void SetNode(JsonObject root, string targetPath, JsonNode? value)
    {
        var normalized = NormalizePath(targetPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        JsonObject current = root;

        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            if (current[segment] is not JsonObject next)
            {
                next = new JsonObject();
                current[segment] = next;
            }

            current = next;
        }

        current[segments[^1]] = value?.DeepClone();
    }

    private static IReadOnlyList<JsonNode?> SelectNodes(JsonNode? node, string path)
    {
        if (node is null)
        {
            return [];
        }

        var normalized = NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [node];
        }

        IReadOnlyList<JsonNode?> currentNodes = [node];
        foreach (var rawSegment in normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var segment = rawSegment.Trim();
            var expandArray = segment.EndsWith("[]", StringComparison.Ordinal);
            var propertyName = expandArray ? segment[..^2] : segment;
            var nextNodes = new List<JsonNode?>();

            foreach (var currentNode in currentNodes)
            {
                if (currentNode is null)
                {
                    continue;
                }

                JsonNode? nextNode = propertyName.Length == 0
                    ? currentNode
                    : currentNode[propertyName];

                if (expandArray)
                {
                    if (nextNode is JsonArray array)
                    {
                        foreach (var item in array)
                        {
                            nextNodes.Add(item);
                        }
                    }
                }
                else if (nextNode is not null)
                {
                    nextNodes.Add(nextNode);
                }
            }

            currentNodes = nextNodes;
            if (currentNodes.Count == 0)
            {
                break;
            }
        }

        return currentNodes;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim();
        if (normalized == "$")
        {
            return string.Empty;
        }

        if (normalized.StartsWith("$.", StringComparison.Ordinal))
        {
            return normalized[2..];
        }

        if (normalized.StartsWith("$", StringComparison.Ordinal))
        {
            return normalized[1..].TrimStart('.');
        }

        return normalized;
    }
}
