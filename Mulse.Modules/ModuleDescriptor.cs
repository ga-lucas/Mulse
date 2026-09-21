namespace Mulse.Modules;

public sealed record ModuleDescriptor(
    string Id,
    string DisplayName,
    ModuleKind Kind,
    string Description);
