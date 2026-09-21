namespace Mulse.Ui.Models;

public sealed record FlowRunViewModel(
    string FlowId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int PayloadCount,
    IReadOnlyList<string> DeliverModules,
    int ResponsePayloadCount = 0);
