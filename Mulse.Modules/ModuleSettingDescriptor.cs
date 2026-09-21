namespace Mulse.Modules;

public sealed record ModuleSettingDescriptor(
    string Key,
    string Label,
    string Description,
    bool IsRequired,
    ModuleSettingInputKind InputKind = ModuleSettingInputKind.Text,
    string? DefaultValue = null,
    IReadOnlyList<ModuleSettingOption>? Options = null);
