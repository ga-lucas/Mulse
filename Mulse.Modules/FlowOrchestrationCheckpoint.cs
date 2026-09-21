namespace Mulse.Modules;

public sealed class FlowOrchestrationCheckpoint
{
    public string FlowId { get; init; } = string.Empty;

    public string InstanceId { get; init; } = string.Empty;

    public string CorrelationKey { get; init; } = string.Empty;

    public string Bookmark { get; init; } = string.Empty;

    public DateTimeOffset UpdatedAt { get; init; }

    public Dictionary<string, string> StateValues { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public List<FlowOrchestrationPayloadSnapshot> Payloads { get; init; } = [];
}
