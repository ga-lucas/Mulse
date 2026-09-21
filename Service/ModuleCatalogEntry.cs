using Mulse.Modules;

namespace Service;

public sealed record ModuleCatalogEntry(
    string PackageId,
    RuntimePackageSourceKind PackageSourceKind,
    string AssemblyPath,
    ModuleDescriptor Descriptor);
