namespace Mulse.Ui.Models;

public sealed record ModuleSuggestionViewModel(
    string Id,
    string DisplayName,
    string Kind,
    string Description,
    bool IsRecommended,
    IReadOnlyList<ModuleSettingViewModel> Settings,
    IReadOnlyList<string> SupportedFormats,
    IReadOnlyList<string> Protocols,
    IReadOnlyList<string> Capabilities);
