namespace Service.Models;

/// <summary>Represents a runtime-loadable module package.</summary>
public sealed record ModulePackageResponse(
    string Id,
    string AssemblyPath,
    RuntimePackageSourceKind SourceKind,
    bool IsLoaded,
    DateTimeOffset LastLoadedAt,
    int ModuleCount);
