namespace Mulse.Modules;

public sealed class PushFlowTriggerContext
{
    private readonly Action<PushFlowTriggerSignal> _signal;

    public PushFlowTriggerContext(string flowId, string moduleId, Action<PushFlowTriggerSignal> signal)
    {
        FlowId = flowId;
        ModuleId = moduleId;
        _signal = signal;
    }

    public string FlowId { get; }

    public string ModuleId { get; }

    public void Signal(string? reason = null)
    {
        _signal(new PushFlowTriggerSignal(FlowId, ModuleId, reason, DateTimeOffset.UtcNow));
    }
}
