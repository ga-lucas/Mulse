namespace Mulse.Ui.Models;

/// <summary>One fetch/parse source submitted when creating or updating a flow.</summary>
public sealed record FlowSourceRequestViewModel(
    string Id,
    FlowStepRequestViewModel Fetch,
    FlowStepRequestViewModel Parse,
    IReadOnlyList<string> InputSourceIds);
