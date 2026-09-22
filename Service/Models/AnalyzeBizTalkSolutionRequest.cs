using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Payload for analyzing a BizTalk solution, project, or directory for migration.</summary>
public sealed record AnalyzeBizTalkSolutionRequest
{
    /// <summary>The full path to a BizTalk solution, BizTalk project, custom project, or directory on disk.</summary>
    [Required]
    public required string SourcePath { get; init; }

    /// <summary>
    /// Optional additional solution, project, or directory paths to analyze alongside <see cref="SourcePath"/>
    /// as a single combined migration workspace. Large BizTalk applications are often split across multiple
    /// solutions (for example a core interfaces solution, a partner-specific solution, and an SSIS solution)
    /// that reference each other's schemas, maps, and pipelines. Supplying every solution here lets the
    /// importer resolve cross-solution project references instead of analyzing each solution in isolation.
    /// </summary>
    public IReadOnlyList<string> AdditionalSourcePaths { get; init; } = [];

    /// <summary>
    /// Optional additional files or directories containing exported BizTalk binding information
    /// (BindingInfo.xml). Many BizTalk solutions don't check bindings into source control because
    /// they're deployed separately per environment (for example via the BizTalk Administration
    /// Console's "Export Bindings", <c>BTSTask ExportBindings</c>, or a deployment framework's
    /// environment settings). Supplying those exports here lets the importer populate accurate
    /// fetch/deliver transport settings instead of falling back to placeholders.
    /// </summary>
    public IReadOnlyList<string> AdditionalBindingPaths { get; init; } = [];
}
