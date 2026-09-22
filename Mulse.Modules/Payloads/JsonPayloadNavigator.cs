using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mulse.Modules.Payloads;

/// <summary>
/// Shared helpers for navigating and mutating JSON payloads using simple dot-path expressions
/// (with an optional trailing <c>[]</c> segment to expand arrays). Public so that module packages
/// outside <c>Mulse.Modules</c> (built-in feature packs or third-party/OSS module contributions)
/// can reuse the same JSON payload conventions instead of re-implementing path navigation.
/// </summary>
public static class JsonPayloadNavigator
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

    /// <summary>
    /// Returns EVERY node matching <paramref name="path"/> (not just the first), expanding any segment written
    /// with a trailing <c>[]</c> into its array elements. Unlike <see cref="ReadFirstNode"/> the returned nodes
    /// are the live nodes from <paramref name="node"/> (not clones), so callers can still tell which part of the
    /// original document each match came from; clone before mutating. Nested arrays are supported by chaining
    /// <c>[]</c> segments, e.g. <c>orders[].lines[].sku</c>.
    /// </summary>
    public static IReadOnlyList<JsonNode?> ReadAllNodes(JsonNode? node, string path)
    {
        return SelectNodes(node, path);
    }

    /// <summary>
    /// Returns the scalar text of every node matching <paramref name="path"/>, including matches nested inside
    /// arrays. Empty/whitespace matches are preserved so callers can distinguish "no match" (empty result) from
    /// "matched an empty value".
    /// </summary>
    public static IReadOnlyList<string?> ReadAllScalarTexts(JsonNode? node, string path)
    {
        return SelectNodes(node, path)
            .Select(ExtractScalarText)
            .ToArray();
    }

    /// <summary>
    /// Interprets a parsed JSON document as a logical row-set: a top-level array yields one row per element,
    /// anything else yields the document itself as a single row. This is the convention used by the join engine
    /// so that a source which emits one array payload and a source which emits many single-object payloads are
    /// treated identically.
    /// </summary>
    public static IReadOnlyList<JsonNode> ReadRows(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is JsonArray array)
        {
            return array.Where(static item => item is not null).Cast<JsonNode>().ToArray();
        }

        return [node];
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

    /// <summary>
    /// Writes <paramref name="value"/> at <paramref name="targetPath"/>, creating missing intermediate objects
    /// along the way. Intermediate segments may use the trailing <c>[]</c> convention (e.g.
    /// <c>orders[].lineItems</c>): the value is then written into every existing element of that array, and an
    /// empty/missing array is materialized with a single object element so the write always lands somewhere.
    /// A trailing <c>[]</c> on the final segment is ignored (write the array value itself instead).
    /// </summary>
    public static void SetNode(JsonObject root, string targetPath, JsonNode? value)
    {
        var normalized = NormalizePath(targetPath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return;
        }

        var segments = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var current = new List<JsonObject> { root };

        for (var index = 0; index < segments.Length - 1; index++)
        {
            var segment = segments[index];
            var expandArray = segment.EndsWith("[]", StringComparison.Ordinal);
            var propertyName = expandArray ? segment[..^2] : segment;
            var next = new List<JsonObject>();

            foreach (var owner in current)
            {
                if (!expandArray)
                {
                    if (owner[propertyName] is not JsonObject child)
                    {
                        child = new JsonObject();
                        owner[propertyName] = child;
                    }

                    next.Add(child);
                    continue;
                }

                if (owner[propertyName] is not JsonArray array)
                {
                    array = [];
                    owner[propertyName] = array;
                }

                if (array.Count == 0)
                {
                    var seed = new JsonObject();
                    array.Add(seed);
                    next.Add(seed);
                    continue;
                }

                for (var itemIndex = 0; itemIndex < array.Count; itemIndex++)
                {
                    if (array[itemIndex] is JsonObject item)
                    {
                        next.Add(item);
                    }
                    else
                    {
                        var replacement = new JsonObject();
                        array[itemIndex] = replacement;
                        next.Add(replacement);
                    }
                }
            }

            current = next;
        }

        var lastSegment = segments[^1];
        if (lastSegment.EndsWith("[]", StringComparison.Ordinal))
        {
            lastSegment = lastSegment[..^2];
        }

        foreach (var owner in current)
        {
            owner[lastSegment] = value?.DeepClone();
        }
    }

    public static void RemoveNode(JsonObject root, string targetPath)
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
            if (current[segments[index]] is not JsonObject next)
            {
                return;
            }

            current = next;
        }

        current.Remove(segments[^1]);
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
