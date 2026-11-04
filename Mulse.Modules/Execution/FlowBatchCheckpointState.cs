namespace Mulse.Modules.Execution;

public sealed class FlowBatchCheckpointState
{
    public string CollectorId { get; init; } = "";
    public string MetadataKey { get; init; } = "";
    public int TargetCount { get; init; }
    public string LastExecutionId { get; init; } = "";
    public string ScheduleKey { get; init; } = "";
    public DateTimeOffset? DeadlineUtc { get; init; }
    public string? OriginalBatchTime { get; init; }
    public string? PolicyBatchInterval { get; init; }
    public bool Ready { get; init; }
}
