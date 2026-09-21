namespace Mulse.Ui.Models;

public sealed record ModuleSettingViewModel(
    string Key,
    string Label,
    string Description,
    bool IsRequired,
    string InputKind,
    string? DefaultValue,
    IReadOnlyList<ModuleSettingOptionViewModel> Options);
