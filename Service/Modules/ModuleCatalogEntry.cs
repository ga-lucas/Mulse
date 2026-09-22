using Mulse.Modules;

namespace Service.Modules;

public sealed record ModuleCatalogEntry(
    string PackageId,
    RuntimePackageSourceKind PackageSourceKind,
    string AssemblyPath,
    ModuleDescriptor Descriptor);
