using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for analyzing a BizTalk solution, project, or directory for migration.</summary>
public sealed record AnalyzeBizTalkSolutionRequest
{
    /// <summary>The full path to a BizTalk solution, BizTalk project, custom project, or directory on disk.</summary>
    [Required]
    public required string SourcePath { get; init; }
}
