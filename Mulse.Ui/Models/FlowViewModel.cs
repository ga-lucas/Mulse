namespace Mulse.Ui.Models;

public sealed record FlowViewModel(
    string Id,
    bool Enabled,
    string TriggerMode,
    TimeSpan? Interval,
    bool RunOnStartup,
    string InputModule,
    IReadOnlyList<string> AugmentModules,
    IReadOnlyList<string> OutputModules);
