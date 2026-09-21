namespace Mulse.Ui.Models;

public sealed record ModulePackageViewModel(
    string Id,
    string AssemblyPath,
    string SourceKind,
    bool IsLoaded,
    DateTimeOffset LastLoadedAt,
    int ModuleCount);
