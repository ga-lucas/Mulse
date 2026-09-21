using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents a configured orchestration flow exposed by the service.</summary>
public sealed record FlowResponse(
    string Id,
    bool Enabled,
    PipelineTriggerMode TriggerMode,
    TimeSpan? Interval,
    bool RunOnStartup,
    string InputModule,
    IReadOnlyList<string> AugmentModules,
    IReadOnlyList<string> OutputModules,
    ModuleStepResponse Input,
    IReadOnlyList<ModuleStepResponse> Augments,
    IReadOnlyList<ModuleStepResponse> Outputs);
