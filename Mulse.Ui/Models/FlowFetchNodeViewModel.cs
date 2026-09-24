namespace Mulse.Ui.Models;

/// <summary>One configured fetch node in a flow's source graph.</summary>
public sealed record FlowFetchNodeViewModel(
    string Id,
    FlowStepViewModel Fetch,
    IReadOnlyList<string> InputParseIds);
