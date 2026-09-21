namespace Service.Models;

/// <summary>Represents a configured module step inside a flow.</summary>
public sealed record ModuleStepResponse(
    string Module,
    IReadOnlyDictionary<string, string> Settings);
