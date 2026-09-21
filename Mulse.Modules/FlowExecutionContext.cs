namespace Mulse.Modules;

public sealed record FlowExecutionContext(
    string FlowId,
    string ExecutionId,
    DateTimeOffset StartedAt);
