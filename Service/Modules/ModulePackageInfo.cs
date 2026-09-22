namespace Service.Modules;

public sealed record ModulePackageInfo(
    string Id,
    string AssemblyPath,
    RuntimePackageSourceKind SourceKind,
    bool IsLoaded,
    DateTimeOffset LastLoadedAt,
    int ModuleCount);
