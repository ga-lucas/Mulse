using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for importing one generated BizTalk draft flow into the runtime flow catalog.</summary>
public sealed record CreateImportedBizTalkFlowRequest
{
    /// <summary>The full path to the BizTalk solution, project, or directory that should be analyzed.</summary>
    [Required]
    public required string SourcePath { get; init; }

    /// <summary>The suggested flow id of the generated draft flow to import.</summary>
    [Required]
    public required string SuggestedFlowId { get; init; }
}
