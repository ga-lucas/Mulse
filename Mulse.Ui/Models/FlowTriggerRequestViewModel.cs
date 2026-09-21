namespace Mulse.Ui.Models;

public sealed record FlowTriggerRequestViewModel(
    string Mode,
    TimeSpan? Interval,
    bool RunOnStartup);
