namespace Mulse.Ui.Models;

public sealed record AnalyzeBizTalkSolutionRequestViewModel(
    string SourcePath,
    IReadOnlyList<string>? AdditionalBindingPaths = null,
    IReadOnlyList<string>? AdditionalSourcePaths = null);
