namespace Mulse.Modules.Execution;

/// <summary>Durable, opaque source identity. Contains the original resolved fetch settings, not downstream payloads.
/// Receipt data must be serializable strings, never module instances, open connections or process-local handles.</summary>
public sealed record FetchProcessingReceipt(
    string Id,
    string FetchNodeId,
    ModuleStepDefinition Step,
    Dictionary<string, string> Data);

public sealed record FetchProcessingOutcome(bool Succeeded, string? Error = null);
