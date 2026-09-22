namespace Mulse.Modules.Triggers;

public sealed record PushFlowTriggerSignal(
    string FlowId,
    string ModuleId,
    string? Reason,
    DateTimeOffset SignaledAt);
