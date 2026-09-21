namespace Service.Models;

/// <summary>Represents an allowed option for a module setting.</summary>
public sealed record ModuleSettingOptionResponse(
    string Value,
    string Label);
