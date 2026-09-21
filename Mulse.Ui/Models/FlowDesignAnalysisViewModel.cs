namespace Mulse.Ui.Models;

public sealed record FlowDesignAnalysisViewModel(
    string DetectedFormat,
    string? FileName,
    string Preview,
    IReadOnlyList<ModuleSuggestionViewModel> FetchModules,
    IReadOnlyList<ModuleSuggestionViewModel> ParseModules,
    IReadOnlyList<ModuleSuggestionViewModel> OrchestrationAugmentModules,
    IReadOnlyList<ModuleSuggestionViewModel> RenderModules,
    IReadOnlyList<ModuleSuggestionViewModel> DeliverModules,
    IReadOnlyList<FieldMappingSuggestionViewModel> Fields);
