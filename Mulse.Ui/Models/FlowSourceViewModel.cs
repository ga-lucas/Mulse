namespace Mulse.Ui.Models;

/// <summary>One configured fetch/parse source in a flow's source graph.</summary>
public sealed record FlowSourceViewModel(
    string Id,
    FlowStepViewModel Fetch,
    FlowStepViewModel Parse,
    IReadOnlyList<string> InputSourceIds);
