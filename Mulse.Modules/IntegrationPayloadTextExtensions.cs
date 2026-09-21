using System.Text;

namespace Mulse.Modules;

/// <summary>
/// Shared, BOM-safe text decoding for <see cref="IntegrationPayload"/> content. Every module that needs to
/// treat a payload's bytes as text (XML, JSON-as-string, flat file, SOAP, etc.) should use
/// <see cref="GetText"/> instead of calling <c>payload.Content.ToString()"/</c> directly.
/// </summary>
/// <remarks>
/// <see cref="BinaryData.ToString()"/> decodes bytes as UTF-8 but does not strip a leading UTF-8/UTF-16/UTF-32
/// byte order mark - the BOM bytes are decoded into a literal U+FEFF character that gets prepended to the
/// resulting string. Downstream string-based parsers such as <see cref="System.Xml.Linq.XDocument.Parse(string)"/>
/// and <see cref="System.Text.Json.Nodes.JsonNode.Parse(string, System.Text.Json.Nodes.JsonNodeOptions?, System.Text.Json.JsonDocumentOptions)"/>
/// then reject that leading character as invalid content, even though the payload is perfectly well-formed.
/// BOM-prefixed files are extremely common (default output of PowerShell, Notepad, Visual Studio "Save As",
/// and many Windows-adjacent export tools), so this is a real-world compatibility gap rather than an edge case.
/// Routing every text decode through <see cref="StreamReader"/> with byte-order-mark detection enabled fixes
/// this in one place, for every text format, without requiring each module to special-case encodings itself.
/// </remarks>
public static class IntegrationPayloadTextExtensions
{
    /// <summary>Decodes <paramref name="payload"/>'s content as text, detecting and stripping a leading byte order mark if present.</summary>
    public static string GetText(this IntegrationPayload payload) => payload.Content.GetText();

    /// <summary>Decodes <paramref name="content"/> as text, detecting and stripping a leading byte order mark if present.</summary>
    public static string GetText(this BinaryData content)
    {
        using var stream = content.ToStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
