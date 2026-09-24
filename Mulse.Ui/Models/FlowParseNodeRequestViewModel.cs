namespace Mulse.Ui.Models;

/// <summary>One parse node submitted when creating or updating a flow; parses every listed fetch node's raw output.</summary>
public sealed record FlowParseNodeRequestViewModel(
    string Id,
    FlowStepRequestViewModel Parse,
    IReadOnlyList<string> FetchNodeIds);
