namespace Service.Models;

/// <summary>Payload for analyzing sample data in the flow designer.</summary>
public sealed record AnalyzeFlowDesignRequest
{
    /// <summary>The optional file name associated with the uploaded sample.</summary>
    public string? FileName { get; init; }

    /// <summary>The optional content type associated with the uploaded sample.</summary>
    public string? ContentType { get; init; }

    /// <summary>The sample text to inspect; empty for a binary workbook.</summary>
    public required string TextContent { get; init; }

    /// <summary>Optional base64-encoded XLS/XLSX sample, limited to 10 MiB decoded.</summary>
    public string? BinaryContent { get; init; }
}
