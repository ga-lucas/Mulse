namespace Service;

public sealed record FlowExecutionResult(
    string FlowId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int PayloadCount,
    IReadOnlyList<string> OutputModules);
