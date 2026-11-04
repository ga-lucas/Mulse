namespace Mulse.Modules.Execution;

public sealed class FlowBatchCheckpointState
{
    public string CollectorId { get; init; } = "";
    public string MetadataKey { get; init; } = "";
    public int TargetCount { get; init; }
    public string LastExecutionId { get; init; } = "";
    public bool Ready { get; init; }
}
