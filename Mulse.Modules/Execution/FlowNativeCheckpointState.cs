using Mulse.Modules.ControlFlow;

namespace Mulse.Modules.Execution;

public sealed class FlowNativeCheckpointState
{
    public string ModuleId { get; init; } = string.Empty;
    public string LastExecutionId { get; init; } = string.Empty;
    public DateTimeOffset? DeadlineUtc { get; init; }
    public bool RecoveryPending { get; init; }
    public NativeDurableState State { get; init; } = new();
}
