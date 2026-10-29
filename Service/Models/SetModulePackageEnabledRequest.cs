namespace Service.Models;

public sealed record SetModulePackageEnabledRequest(bool Enabled, string? Confirmation = null);
