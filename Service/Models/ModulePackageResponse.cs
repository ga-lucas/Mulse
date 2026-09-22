using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents a runtime-loadable module package.</summary>
public sealed record ModulePackageResponse(
    string Id,
    string AssemblyPath,
    RuntimePackageSourceKind SourceKind,
    bool IsLoaded,
    DateTimeOffset LastLoadedAt,
    int ModuleCount)
{
    /// <summary>Maps a runtime <see cref="ModulePackageInfo"/> to its API response shape.</summary>
    public static ModulePackageResponse From(ModulePackageInfo package)
        => new(package.Id, package.AssemblyPath, package.SourceKind, package.IsLoaded, package.LastLoadedAt, package.ModuleCount);
}
