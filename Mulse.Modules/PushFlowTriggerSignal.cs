namespace Mulse.Modules;

public sealed record PushFlowTriggerSignal(
    string FlowId,
    string ModuleId,
    string? Reason,
    DateTimeOffset SignaledAt);
