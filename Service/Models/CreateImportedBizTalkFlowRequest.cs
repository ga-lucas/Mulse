using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for importing one generated BizTalk draft flow into the runtime flow catalog.</summary>
public sealed record CreateImportedBizTalkFlowRequest
{
    /// <summary>The full path to the BizTalk solution, project, or directory that should be analyzed.</summary>
    [Required]
    public required string SourcePath { get; init; }

    /// <summary>
    /// Optional additional solution, project, or directory paths that were combined with <see cref="SourcePath"/>
    /// during analysis (see <see cref="AnalyzeBizTalkSolutionRequest.AdditionalSourcePaths"/>). Must match what
    /// was passed to analyze so the same draft flow candidate (and its cross-solution references) can be
    /// reproduced at import time.
    /// </summary>
    public IReadOnlyList<string> AdditionalSourcePaths { get; init; } = [];

    /// <summary>The suggested flow id of the generated draft flow to import.</summary>
    [Required]
    public required string SuggestedFlowId { get; init; }
}
