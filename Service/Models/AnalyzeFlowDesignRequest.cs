using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for analyzing sample data in the flow designer.</summary>
public sealed record AnalyzeFlowDesignRequest
{
    /// <summary>The optional file name associated with the uploaded sample.</summary>
    public string? FileName { get; init; }

    /// <summary>The optional content type associated with the uploaded sample.</summary>
    public string? ContentType { get; init; }

    /// <summary>The sample payload content to inspect.</summary>
    [Required]
    public required string TextContent { get; init; }
}
