using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mulse.CompatibilityPack;

internal static class FlatFileCodec
{
    public static char ResolveDelimiter(IReadOnlyDictionary<string, string> settings, string moduleId)
    {
        var mode = settings.TryGetValue("delimiter", out var configured) && !string.IsNullOrWhiteSpace(configured)
            ? configured.Trim()
            : "Comma";

        return mode.ToLowerInvariant() switch
        {
            "comma" => ',',
            "pipe" => '|',
            "semicolon" => ';',
            "tab" => '\t',
            "custom" => ResolveCustomDelimiter(settings, moduleId),
            _ => throw new ArgumentException($"Module '{moduleId}' has an unsupported delimiter setting '{mode}'.", nameof(settings))
        };
    }

    public static char ResolveQuoteCharacter(IReadOnlyDictionary<string, string> settings)
    {
        if (!settings.TryGetValue("quoteCharacter", out var configured) || string.IsNullOrEmpty(configured))
        {
            return '"';
        }

        return configured[0];
    }

    public static IReadOnlyList<string[]> ParseRecords(string text, char delimiter, char quoteCharacter, bool trimValues)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var records = new List<string[]>(lines.Length);

        foreach (var line in lines)
        {
            var fields = ParseLine(line, delimiter, quoteCharacter);
            if (trimValues)
            {
                for (var index = 0; index < fields.Count; index++)
                {
                    fields[index] = fields[index].Trim();
                }
            }

            records.Add(fields.ToArray());
        }

        return records;
    }

    public static string Render(JsonNode root, char delimiter, char quoteCharacter, bool includeHeader)
    {
        var records = NormalizeRecords(root);
        if (records.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        if (includeHeader)
        {
            builder.AppendLine(string.Join(delimiter, records[0].Keys.Select(key => Escape(key, delimiter, quoteCharacter))));
        }

        foreach (var record in records)
        {
            builder.AppendLine(string.Join(delimiter, record.Values.Select(value => Escape(value, delimiter, quoteCharacter))));
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    public static JsonNode ToJson(IReadOnlyList<string[]> records, bool hasHeader)
    {
        if (records.Count == 0)
        {
            return new JsonObject { ["records"] = new JsonArray() };
        }

        var firstRecord = records[0];
        var headers = hasHeader
            ? firstRecord.Select((value, index) => string.IsNullOrWhiteSpace(value) ? $"Column{index + 1}" : value).ToArray()
            : Enumerable.Range(0, firstRecord.Length).Select(index => $"Column{index + 1}").ToArray();

        var dataRecords = hasHeader ? records.Skip(1) : records;
        var array = new JsonArray();
        foreach (var record in dataRecords)
        {
            var node = new JsonObject();
            for (var index = 0; index < headers.Length; index++)
            {
                node[headers[index]] = index < record.Length ? record[index] : string.Empty;
            }

            array.Add(node);
        }

        return new JsonObject { ["records"] = array };
    }

    private static char ResolveCustomDelimiter(IReadOnlyDictionary<string, string> settings, string moduleId)
    {
        if (!settings.TryGetValue("customDelimiter", out var customDelimiter) || string.IsNullOrEmpty(customDelimiter))
        {
            throw new ArgumentException($"Module '{moduleId}' requires the 'customDelimiter' setting when delimiter is Custom.", nameof(settings));
        }

        return customDelimiter[0];
    }

    private static List<string> ParseLine(string line, char delimiter, char quoteCharacter)
    {
        var fields = new List<string>();
        var builder = new StringBuilder();
        var inQuotes = false;

        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (character == quoteCharacter)
            {
                if (inQuotes && index + 1 < line.Length && line[index + 1] == quoteCharacter)
                {
                    builder.Append(quoteCharacter);
                    index++;
                    continue;
                }

                inQuotes = !inQuotes;
                continue;
            }

            if (character == delimiter && !inQuotes)
            {
                fields.Add(builder.ToString());
                builder.Clear();
                continue;
            }

            builder.Append(character);
        }

        fields.Add(builder.ToString());
        return fields;
    }

    private static IReadOnlyList<Dictionary<string, string>> NormalizeRecords(JsonNode root)
    {
        var array = root["records"] as JsonArray ?? root as JsonArray;
        if (array is null)
        {
            throw new InvalidOperationException("Flat-file rendering requires a JSON array or an object containing a 'records' array.");
        }

        var records = new List<Dictionary<string, string>>(array.Count);
        foreach (var item in array)
        {
            switch (item)
            {
                case JsonObject jsonObject:
                    records.Add(jsonObject.ToDictionary(
                        static property => property.Key,
                        static property => CompatibilityPayloadNavigator.ExtractScalarText(property.Value) ?? string.Empty,
                        StringComparer.OrdinalIgnoreCase));
                    break;
                case JsonArray jsonArray:
                    var indexed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (var index = 0; index < jsonArray.Count; index++)
                    {
                        indexed[$"Column{index + 1}"] = CompatibilityPayloadNavigator.ExtractScalarText(jsonArray[index]) ?? string.Empty;
                    }

                    records.Add(indexed);
                    break;
                default:
                    throw new InvalidOperationException("Flat-file rendering requires each record to be a JSON object or JSON array.");
            }
        }

        return records;
    }

    private static string Escape(string value, char delimiter, char quoteCharacter)
    {
        var requiresQuoting = value.Contains(delimiter) || value.Contains(quoteCharacter) || value.Contains('\n') || value.Contains('\r');
        if (!requiresQuoting)
        {
            return value;
        }

        return string.Concat(quoteCharacter, value.Replace(quoteCharacter.ToString(), new string(quoteCharacter, 2), StringComparison.Ordinal), quoteCharacter);
    }
}
