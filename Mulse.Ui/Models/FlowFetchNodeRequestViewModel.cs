namespace Mulse.Ui.Models;

/// <summary>One fetch node submitted when creating or updating a flow.</summary>
public sealed record FlowFetchNodeRequestViewModel(
    string Id,
    FlowStepRequestViewModel Fetch,
    IReadOnlyList<string> InputParseIds);
