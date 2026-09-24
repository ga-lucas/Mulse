namespace Mulse.Ui.Models;

/// <summary>One configured parse node in a flow's source graph, parsing the raw output of one or more fetch nodes.</summary>
public sealed record FlowParseNodeViewModel(
    string Id,
    FlowStepViewModel Parse,
    IReadOnlyList<string> FetchNodeIds);
