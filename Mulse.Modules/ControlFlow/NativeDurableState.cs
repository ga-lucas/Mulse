namespace Mulse.Modules.ControlFlow;

public sealed class NativeDurableState
{
    public int Version { get; set; } = 1;
    public string DefinitionHash { get; set; } = "";
    public string Status { get; set; } = "Running";
    public string? Failure { get; set; }
    public string? EffectNodeId { get; set; }
    public NativeFlowNodeKind? EffectKind { get; set; }
    public string? EffectModule { get; set; }
    public int Operations { get; set; }
    public List<NativeDurableCursor> Stack { get; set; } = [];
    public List<string> AcceptedMessageIds { get; set; } = [];
    public Dictionary<string, string> InitialMetadata { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, List<FlowOrchestrationPayloadSnapshot>> Messages { get; set; } = new(StringComparer.Ordinal);
    public List<FlowOrchestrationPayloadSnapshot> Responses { get; set; } = [];
    public List<NativeCommittedEffectEvidence> CommittedEffects { get; set; } = [];
    public Dictionary<string, NativeScalar> Variables { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, NativeScalarType> VariableTypes { get; set; } = new(StringComparer.Ordinal);
}

public sealed record NativeCommittedEffectEvidence(string NodeId, int Operation, DateTimeOffset ConfirmedAtUtc, string OperatorEvidence);

public sealed class NativeDurableCursor
{
    public string NodeId { get; set; } = "";
    public int Position { get; set; }
    public int Phase { get; set; }
    public int Iterations { get; set; }
    public DateTimeOffset? DeadlineUtc { get; set; }
    public List<FlowOrchestrationPayloadSnapshot> Inputs { get; set; } = [];
    public List<FlowOrchestrationPayloadSnapshot> Outputs { get; set; } = [];
}

public sealed class NativeAmbiguousEffectException(string message, Exception? inner = null) : InvalidOperationException(message, inner);
public sealed class NativePersistenceException(string message, Exception inner) : IOException(message, inner);
public sealed class NativeUnmatchedMessageException(string message) : InvalidOperationException(message);
/// <summary>An explicit Terminate. It is deliberately not catchable by a native Try handler.</summary>
public sealed class NativeTerminationException(string message) : InvalidOperationException(message);
