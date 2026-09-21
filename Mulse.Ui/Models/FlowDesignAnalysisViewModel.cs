namespace Mulse.Ui.Models;

public sealed record FlowDesignAnalysisViewModel(
    string DetectedFormat,
    string? FileName,
    string Preview,
    IReadOnlyList<ModuleSuggestionViewModel> InputModules,
    IReadOnlyList<ModuleSuggestionViewModel> OrchestrationAugmentModules,
    IReadOnlyList<ModuleSuggestionViewModel> OutputModules,
    IReadOnlyList<FieldMappingSuggestionViewModel> Fields);
