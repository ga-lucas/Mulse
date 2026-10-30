namespace Mulse.Ui.Models;

public sealed record BizTalkProjectReferenceViewModel(
    string Name,
    string Include,
    string? HintPath,
    string Resolution,
    string? ResolvedProjectPath,
    IReadOnlyList<string> CandidateProjectPaths,
    string Description);
