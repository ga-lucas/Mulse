using System.Text.Json;

namespace Mulse.Modules.ControlFlow;

public sealed record NativePendingEffect(string EffectId, NativeFlowNodeKind Kind, string Module)
{
    public string NodeId => EffectId;
    public bool RequiresOutput => true;
}

/// <summary>Explicit verified-commit reconciliation only. The host must hold the same per-flow lock used by execution.</summary>
public sealed class NativeDurableRecovery(IFlowOrchestrationStateStore store, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    public static NativePendingEffect? Inspect(FlowOrchestrationCheckpoint checkpoint)
    {
        var native = checkpoint.Native;
        if (native is not { RecoveryPending: true } || native.ModuleId != NativeFlowDefinition.ModuleId
            || native.State is not { EffectNodeId: { Length: > 0 } id, EffectKind: { } kind, EffectModule: { Length: > 0 } module } state
            || state.Status is not ("Running" or "Faulted") || state.Stack?.LastOrDefault()?.NodeId != id
            || id.Length > 128 || module.Length > 256
            || kind is not (NativeFlowNodeKind.Augment or NativeFlowNodeKind.Render or NativeFlowNodeKind.Deliver)) return null;
        return new(id, kind, module);
    }

    public static async Task ReconcileCommittedAsync(FlowOrchestrationCheckpoint checkpoint, NativeFlowDefinition definition,
        string expectedEffectId, string evidence, IntegrationBatch? output, IFlowOrchestrationStateStore store, TimeProvider clock,
        CancellationToken token)
    {
        var current = await store.GetAsync(checkpoint.FlowId, checkpoint.CorrelationKey, token).ConfigureAwait(false);
        if (current is null || current.InstanceId != checkpoint.InstanceId || current.Native?.State is not { } state
            || checkpoint.Native?.State is not { } expected || state.EffectNodeId != expected.EffectNodeId
            || state.Operations != expected.Operations || state.DefinitionHash != expected.DefinitionHash)
            throw new InvalidOperationException("Inspected native effect checkpoint is stale; inspect the current intent before confirmation.");
        await new NativeDurableRecovery(store, clock).ConfirmCommittedAsync(
            checkpoint.FlowId, checkpoint.CorrelationKey, definition, expectedEffectId, evidence, output, token).ConfigureAwait(false);
    }

    public async Task<NativePendingEffect?> GetPendingEffectAsync(string flowId, string correlationKey,
        NativeFlowDefinition definition, CancellationToken cancellationToken)
    {
        var (checkpoint, nodes, _) = await ReadAsync(flowId, correlationKey, definition, cancellationToken).ConfigureAwait(false);
        var state = checkpoint.Native!.State;
        if (state.EffectNodeId is null) return null;
        var node = nodes[state.EffectNodeId];
        return new(node.Id, node.Kind, node.Module);
    }

    public async Task ConfirmCommittedAsync(string flowId, string correlationKey, NativeFlowDefinition definition,
        string expectedEffectId, string operatorEvidence, IntegrationBatch? output, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedEffectId) || expectedEffectId.Length > 128)
            throw new ArgumentException("Verified commit requires an expected effect ID.", nameof(expectedEffectId));
        if (string.IsNullOrWhiteSpace(operatorEvidence) || operatorEvidence.Length > 4096)
            throw new ArgumentException("Verified commit requires nonblank operator evidence of at most 4096 characters.", nameof(operatorEvidence));
        var (checkpoint, nodes, pinned) = await ReadAsync(flowId, correlationKey, definition, cancellationToken).ConfigureAwait(false);
        var native = checkpoint.Native!; var state = native.State;
        if (!native.RecoveryPending || state.Status is not ("Running" or "Faulted") || state.EffectNodeId != expectedEffectId)
            throw new InvalidOperationException("Expected native effect is not in flight. Stale IDs, completion holds and non-effect faults cannot be reconciled.");
        var node = nodes[expectedEffectId];
        if (node.Kind is not (NativeFlowNodeKind.Augment or NativeFlowNodeKind.Render or NativeFlowNodeKind.Deliver))
            throw new NotSupportedException("Only verified committed native external effects can be reconciled.");
        if (output is null || output.Payloads is null)
            throw new ArgumentException("Verified commit requires explicit output: transform output for Augment/Render, or response output (empty for verified no response) for Deliver.", nameof(output));
        if (state.CommittedEffects.Count >= 10000) throw new InvalidOperationException("Native continuation exceeds its verified-commit audit limit.");
        var snapshots = output.Payloads.Select(payload => FlowOrchestrationPayloadSnapshot.Capture(payload, preserveMetadataCase: true)).ToList();
        var working = checkpoint.Payloads;
        if (node.Kind == NativeFlowNodeKind.Deliver) state.Responses.AddRange(snapshots);
        else if (node.InputMessages is not null)
        {
            if (snapshots.Count != 1) throw new ArgumentException("Verified named transform output requires exactly one payload.", nameof(output));
            state.Messages[node.MessageName] = snapshots;
        }
        else working = snapshots;
        state.CommittedEffects.Add(new(expectedEffectId, state.Operations, (clock ?? TimeProvider.System).GetUtcNow(), operatorEvidence.Trim()));
        state.EffectNodeId = null; state.EffectKind = null; state.EffectModule = null;
        state.Status = "Running"; state.Failure = null;
        state.Stack.RemoveAt(state.Stack.Count - 1);
        NativeDurableExecutor.ValidateState(state, pinned, nodes, working);
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var top = state.Stack.LastOrDefault();
        var reconciled = new FlowOrchestrationCheckpoint
        {
            FlowId = checkpoint.FlowId, InstanceId = checkpoint.InstanceId, CorrelationKey = checkpoint.CorrelationKey,
            Bookmark = "native:" + (top?.NodeId is { Length: > 0 } id ? id : "start"), UpdatedAt = now,
            StateValues = checkpoint.StateValues, Payloads = working, SourceReceipts = checkpoint.SourceReceipts,
            Native = new()
            {
                ModuleId = native.ModuleId, LastExecutionId = native.LastExecutionId, DeadlineUtc = now,
                RecoveryPending = false, State = state
            }
        };
        // Validate and detach the entire candidate before crossing the persistence boundary.
        reconciled = NativeDurableExecutor.Clone(reconciled);
        cancellationToken.ThrowIfCancellationRequested();
        try { await store.UpsertAsync(reconciled, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception)
        { throw new NativePersistenceException("Native verified-commit acknowledgement failed. Inspect persisted state before any further operator action; no effect was replayed.", exception); }
    }

    private async Task<(FlowOrchestrationCheckpoint Checkpoint, Dictionary<string, NativeFlowNode> Nodes, NativeFlowDefinition Definition)> ReadAsync(
        string flowId, string correlationKey, NativeFlowDefinition definition, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(flowId) || string.IsNullOrWhiteSpace(correlationKey))
            throw new ArgumentException("Native recovery requires exact flow and persisted correlation identities.");
        var pinned = NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json));
        if (!pinned.Durable) throw new ArgumentException("Native recovery requires a durable definition.");
        var stored = await store.GetAsync(flowId, correlationKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Native recovery checkpoint does not exist; no instance was created.");
        var checkpoint = NativeDurableExecutor.Clone(stored);
        if (checkpoint.FlowId != flowId || checkpoint.CorrelationKey != correlationKey || checkpoint.Native is not { } native
            || native.ModuleId != NativeFlowDefinition.ModuleId || checkpoint.Odx is not null || checkpoint.Batch is not null
            || native.State is null || native.State.Version != 1 || native.State.DefinitionHash != NativeDurableExecutor.DefinitionHash(pinned))
            throw new InvalidOperationException("Native recovery runtime, identity or pinned definition mismatch; no state was modified.");
        var nodes = pinned.AllNodes().ToDictionary(node => node.Id, StringComparer.Ordinal);
        NativeDurableExecutor.ValidateState(native.State, pinned, nodes, checkpoint.Payloads);
        return (checkpoint, nodes, pinned);
    }
}
