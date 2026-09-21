using System.ComponentModel.DataAnnotations;
using Mulse.Modules;

namespace Service.Models;

/// <summary>Represents the trigger settings used when creating or updating a flow.</summary>
public sealed record FlowTriggerRequest
{
    /// <summary>The trigger mode for the flow.</summary>
    [Required]
    public required PipelineTriggerMode Mode { get; init; }

    /// <summary>The interval between runs when the flow is interval driven.</summary>
    public TimeSpan? Interval { get; init; }

    /// <summary>Whether the flow should execute immediately when the service starts.</summary>
    public bool RunOnStartup { get; init; }
}
