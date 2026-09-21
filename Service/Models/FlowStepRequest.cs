using System.ComponentModel.DataAnnotations;

namespace Service.Models;

/// <summary>Represents a module step provided when creating or updating a flow.</summary>
public sealed record FlowStepRequest
{
    /// <summary>The module id to execute for this step.</summary>
    [Required]
    public required string Module { get; init; }

    /// <summary>The configuration settings passed to the module step.</summary>
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional retry policy override for this step. Falls back to the flow's default retry policy when omitted.</summary>
    public RetryPolicyRequest? Retry { get; init; }
}
