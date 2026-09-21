using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mulse.Hl7;

internal static class Hl7Codec
{
    public static Hl7MessageModel Parse(string text)
    {
        var normalized = text.Replace("\r\n", "\r", StringComparison.Ordinal).Replace('\n', '\r');
        var lines = normalized.Split('\r', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("HL7 parsing requires at least one segment.");
        }

        var msh = lines[0];
        if (!msh.StartsWith("MSH", StringComparison.Ordinal) || msh.Length < 8)
        {
            throw new InvalidOperationException("HL7 parsing requires the first segment to be MSH with standard encoding characters.");
        }

        var fieldSeparator = msh[3];
        var encodingCharacters = msh.Substring(4, 4);
        var separators = new Hl7SeparatorsModel(
            fieldSeparator.ToString(),
            encodingCharacters[0].ToString(),
            encodingCharacters[1].ToString(),
            encodingCharacters[2].ToString(),
            encodingCharacters[3].ToString());

        var segments = new List<Hl7SegmentModel>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Length < 3)
            {
                continue;
            }

            var name = line[..3];
            var fields = line.Length > 4
                ? line.Substring(4).Split(fieldSeparator).ToArray()
                : [];
            segments.Add(new Hl7SegmentModel(name, fields));
        }

        return new Hl7MessageModel(separators, segments);
    }

    public static string Render(Hl7MessageModel message)
    {
        if (message.Segments.Count == 0)
        {
            return string.Empty;
        }

        var fieldSeparator = message.Separators.Field.Single();
        var lines = message.Segments.Select(segment => segment.Name + fieldSeparator + string.Join(fieldSeparator, segment.Fields));
        return string.Join('\r', lines) + '\r';
    }

    public static string Serialize(Hl7MessageModel message)
        => JsonSerializer.Serialize(message, SerializerOptions);

    public static Hl7MessageModel Deserialize(string json)
        => JsonSerializer.Deserialize<Hl7MessageModel>(json, SerializerOptions)
            ?? throw new InvalidOperationException("HL7 rendering requires a valid HL7 JSON payload.");

    public static IReadOnlyDictionary<string, string> CreateMetadata(Hl7MessageModel message)
    {
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["segmentCount"] = message.Segments.Count.ToString(),
            ["messageType"] = string.Empty,
            ["triggerEvent"] = string.Empty,
            ["hl7Version"] = string.Empty
        };

        var msh = message.Segments.FirstOrDefault(segment => string.Equals(segment.Name, "MSH", StringComparison.Ordinal));
        if (msh is null)
        {
            return metadata;
        }

        if (msh.Fields.Count > 7)
        {
            var components = msh.Fields[7].Split(message.Separators.Component.Single());
            metadata["messageType"] = components.Length > 0 ? components[0] : string.Empty;
            metadata["triggerEvent"] = components.Length > 1 ? components[1] : string.Empty;
        }

        if (msh.Fields.Count > 10)
        {
            metadata["hl7Version"] = msh.Fields[10];
        }

        return metadata;
    }

    private static JsonSerializerOptions SerializerOptions { get; } = new()
    {
        WriteIndented = true
    };
}

internal sealed record Hl7MessageModel(Hl7SeparatorsModel Separators, IReadOnlyList<Hl7SegmentModel> Segments);

internal sealed record Hl7SeparatorsModel(string Field, string Component, string Repeat, string Escape, string Subcomponent);

internal sealed record Hl7SegmentModel(string Name, IReadOnlyList<string> Fields);
