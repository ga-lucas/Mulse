namespace Service.Models;

/// <summary>Represents the result of manually executing a configured flow.</summary>
public sealed record FlowRunResponse(
    string FlowId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int PayloadCount,
    IReadOnlyList<string> OutputModules);
