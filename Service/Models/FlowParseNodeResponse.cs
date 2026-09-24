namespace Service.Models;

/// <summary>Represents one configured parse node in a flow's source graph, parsing the raw output of one or more fetch nodes.</summary>
public sealed record FlowParseNodeResponse(
    string Id,
    ModuleStepResponse Parse,
    IReadOnlyList<string> FetchNodeIds);
