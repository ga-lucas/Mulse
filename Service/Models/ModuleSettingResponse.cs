namespace Service.Models;

/// <summary>Represents a UI-configurable module setting.</summary>
public sealed record ModuleSettingResponse(
    string Key,
    string Label,
    string Description,
    bool IsRequired,
    string InputKind,
    string? DefaultValue,
    IReadOnlyList<ModuleSettingOptionResponse> Options);
