namespace Service.Flows;

public sealed record FlowExecutionResult(
    string FlowId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    int PayloadCount,
    IReadOnlyList<string> DeliverModules,
    FlowExecutionOutcome Outcome,
    string? Detail,
    IReadOnlyList<IntegrationPayload>? ResponsePayloads = null,
    DateTimeOffset? NextAttemptAt = null);
