namespace Service.Models;

/// <summary>Represents one configured fetch/parse source in a flow's source graph.</summary>
public sealed record FlowSourceResponse(
    string Id,
    ModuleStepResponse Fetch,
    ModuleStepResponse Parse,
    IReadOnlyList<string> InputSourceIds);
