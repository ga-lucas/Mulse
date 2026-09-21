namespace Mulse.Modules;

public sealed class PipelineTriggerOptions
{
    public PipelineTriggerMode Mode { get; init; } = PipelineTriggerMode.OnDemand;

    public TimeSpan? Interval { get; init; }

    public bool RunOnStartup { get; init; }
}
