namespace Mulse.Modules.Execution;

/// <summary>Terminal processing result retained while source callbacks are pending. Delivery is never replayed from this state.</summary>
public sealed class FlowSourceCompletion
{
    public FetchProcessingOutcome Outcome { get; init; } = new(true);
    public bool ThrowProcessingFailure { get; init; }
    public int PayloadCount { get; init; }
    public List<string> DeliverModules { get; init; } = [];
    public List<FlowOrchestrationPayloadSnapshot> Responses { get; init; } = [];
    public List<string> CallbackErrors { get; init; } = [];
    public List<string> CompletedReceiptIds { get; init; } = [];
    public bool NotificationsExhausted { get; set; }
    public string? CheckpointFlowId { get; init; }
    public string? CheckpointCorrelationKey { get; init; }
}
