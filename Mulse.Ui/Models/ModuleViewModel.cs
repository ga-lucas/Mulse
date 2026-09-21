namespace Mulse.Ui.Models;

public sealed record ModuleViewModel(
    string Id,
    string DisplayName,
    string Kind,
    string Description,
    IReadOnlyList<ModuleSettingViewModel> Settings,
    IReadOnlyList<string> SupportedFormats,
    IReadOnlyList<string> Protocols,
    IReadOnlyList<string> Capabilities);
