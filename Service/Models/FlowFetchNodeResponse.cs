namespace Service.Models;

/// <summary>Represents one configured fetch node in a flow's source graph.</summary>
public sealed record FlowFetchNodeResponse(
    string Id,
    ModuleStepResponse Fetch,
    IReadOnlyList<string> InputParseIds);
