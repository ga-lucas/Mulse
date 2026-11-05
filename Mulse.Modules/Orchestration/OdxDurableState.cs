using System.Text.Json.Nodes;

namespace Mulse.Modules.Orchestration;

public sealed record OdxReceiveSubscription(string Port, string MessageName, IReadOnlyDictionary<string, string> Correlation)
{
    public IReadOnlyList<string> RequiredMetadata { get; init; } = [];
    public string? OperationName { get; init; }
    public string? OperationMessageName { get; init; }
}
public sealed record OdxIncomingMessage(string Id, string Port, IntegrationBatch Batch, IReadOnlyList<FetchProcessingReceipt> SourceReceipts)
{
    public string? OperationName { get; init; }
    public string? OperationMessageName { get; init; }
}
public delegate OdxReceiveSubscription OdxReceiveFactory(OdxNode node, OdxExecutionFrame frame);
public delegate DateTimeOffset OdxDeadlineFactory(OdxNode node, OdxExecutionFrame frame);

public sealed class OdxDurableBindings
{
    public OdxExecutionBindings Execution { get; } = new();
    public Dictionary<int, OdxReceiveFactory> Receives { get; } = [];
    public Dictionary<int, OdxDeadlineFactory> Deadlines { get; } = [];
    public Dictionary<int, Func<IntegrationPayload, bool>> ReceiveFilters { get; } = [];
    /// <summary>Deterministic frame-local updates performed after a receive and checkpointed with its acceptance.</summary>
    public Dictionary<int, OdxActivity> AcceptedReceives { get; } = [];
    /// <summary>Deterministic correlation validation and pinning before a send's external-effect intent is saved.</summary>
    public Dictionary<int, OdxActivity> PreparingSends { get; } = [];

    /// <summary>Only deterministic, frame-local activities may opt out of the external-effect intent journal.</summary>
    public HashSet<int> FrameLocalActivities { get; } = [];
}

public enum OdxDurableOutcome { Waiting, Completed, Unmatched }
public sealed record OdxDurableResult(OdxDurableOutcome Outcome, DateTimeOffset? NextDeadlineUtc);
public sealed class OdxAmbiguousEffectException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
public sealed class OdxPersistenceException(string message, Exception inner) : IOException(message, inner);
public sealed class OdxUnmatchedMessageException(string message) : InvalidOperationException(message);

internal sealed class OdxDurableState
{
    public int Version { get; set; } = 1;
    public string DefinitionHash { get; set; } = "";
    public int EntryId { get; set; }
    public string Status { get; set; } = "Running";
    public string? Failure { get; set; }
    public int? EffectNodeId { get; set; }
    public int Operations { get; set; }
    public List<OdxCursor> Stack { get; set; } = [];
    public List<OdxWaitChoice> Waits { get; set; } = [];
    public HashSet<string> AcceptedMessages { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonNode?> Variables { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<FlowOrchestrationPayloadSnapshot>> Messages { get; set; } = new(StringComparer.Ordinal);
    public List<FlowOrchestrationPayloadSnapshot> Responses { get; set; } = [];
}

internal sealed class OdxCursor
{
    public int NodeId { get; set; }
    public int Position { get; set; }
    public int Iterations { get; set; }
    public int? HandlerId { get; set; }
    public OdxSavedFault? Fault { get; set; }
}

internal sealed class OdxWaitChoice
{
    public int TriggerId { get; set; }
    public int? BranchId { get; set; }
    public string? Port { get; set; }
    public string? MessageName { get; set; }
    public string? OperationName { get; set; }
    public string? OperationMessageName { get; set; }
    public Dictionary<string, string> Correlation { get; set; } = new(StringComparer.Ordinal);
    public List<string> RequiredMetadata { get; set; } = [];
    public DateTimeOffset? DeadlineUtc { get; set; }
}

internal sealed record OdxSavedFault(string Type, string Message, string? OriginalStackTrace);
internal sealed class OdxRestoredException(OdxSavedFault fault) : Exception(fault.Message)
{
    public string OriginalType => fault.Type;
    public override string? StackTrace => fault.OriginalStackTrace;
}
