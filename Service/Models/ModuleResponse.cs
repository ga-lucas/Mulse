using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents a module that can participate in a Mulse flow.</summary>
public sealed record ModuleResponse(
    string Id,
    string DisplayName,
    ModuleKind Kind,
    string Description,
    string PackageId,
    RuntimePackageSourceKind PackageSourceKind,
    string AssemblyPath,
    IReadOnlyList<ModuleSettingResponse> Settings,
    IReadOnlyList<string> SupportedFormats,
    IReadOnlyList<string> Protocols,
    IReadOnlyList<string> Capabilities);
