namespace Mulse.Ui.Models;

public sealed record BizTalkCustomAssemblyViewModel(
    string Name,
    string ProjectPath,
    int SourceFileCount,
    string SuggestedModuleKind,
    string MigrationApproach,
    bool WrapperRecommended,
    IReadOnlyList<string> UsedByProjects);
