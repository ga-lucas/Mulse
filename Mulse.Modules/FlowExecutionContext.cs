namespace Mulse.Modules;

public sealed class FlowExecutionContext
{
    private readonly Dictionary<string, string> _orchestrationState = new(StringComparer.OrdinalIgnoreCase);

    public FlowExecutionContext(string flowId, string executionId, DateTimeOffset startedAt)
    {
        FlowId = flowId;
        ExecutionId = executionId;
        StartedAt = startedAt;
    }

    public string FlowId { get; }

    public string ExecutionId { get; }

    public DateTimeOffset StartedAt { get; }

    public FlowExecutionDisposition Disposition { get; private set; } = FlowExecutionDisposition.Continue;

    public string? SuspensionReason { get; private set; }

    public string? OrchestrationInstanceId { get; private set; }

    public string? OrchestrationCorrelationKey { get; private set; }

    public string? OrchestrationBookmark { get; private set; }

    public bool IsResumedOrchestration { get; private set; }

    public IReadOnlyDictionary<string, string> OrchestrationState => _orchestrationState;

    public (string FlowId, string CorrelationKey)? PendingCheckpointCompletion { get; private set; }

    public void SetOrchestrationInstance(string instanceId, string correlationKey, string bookmark, bool isResumed)
    {
        OrchestrationInstanceId = instanceId;
        OrchestrationCorrelationKey = correlationKey;
        OrchestrationBookmark = bookmark;
        IsResumedOrchestration = isResumed;
    }

    public void ReplaceOrchestrationState(IReadOnlyDictionary<string, string> values)
    {
        _orchestrationState.Clear();
        foreach (var entry in values)
        {
            _orchestrationState[entry.Key] = entry.Value;
        }
    }

    public void Suspend(string reason)
    {
        Disposition = FlowExecutionDisposition.Suspended;
        SuspensionReason = reason;
    }

    public void MarkCheckpointForCompletion(string flowId, string correlationKey)
    {
        PendingCheckpointCompletion = (flowId, correlationKey);
    }
}
