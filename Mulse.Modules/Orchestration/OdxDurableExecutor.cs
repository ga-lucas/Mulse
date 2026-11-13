using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mulse.Modules.Orchestration;

/// <summary>Single-owner, persisted ODX continuation. Call under the host's per-flow execution lock.</summary>
public sealed class OdxDurableExecutor(IFlowOrchestrationStateStore store, TimeProvider clock)
{
    private const string StateKey = "odx.continuation.v1";
    private const int MaxStateCharacters = 24_000_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 256 };

    public async Task<OdxDurableResult> ExecuteAsync(OdxProgram program, OdxNode entry, OdxDurableBindings bindings,
        OdxExecutionFrame frame, string correlationKey, string bindingVersion, OdxIncomingMessage? incoming, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(frame.Program, program)) throw new ArgumentException("The frame must reference the executed source model.");
        if (string.IsNullOrWhiteSpace(correlationKey) || string.IsNullOrWhiteSpace(bindingVersion))
            throw new ArgumentException("Durable execution requires an instance correlation key and a pinned binding version.");
        if (frame.Context.Disposition != FlowExecutionDisposition.Continue) throw new InvalidOperationException("Resume requires a fresh, unsuspended execution context.");
        if (incoming is not null && (string.IsNullOrWhiteSpace(incoming.Id) || string.IsNullOrWhiteSpace(incoming.Port) || incoming.Batch.Count != 1))
            throw new ArgumentException("A correlated receive requires a stable message ID, port and exactly one payload.");
        var execution = new OdxExecutionBindings();
        foreach (var pair in bindings.Execution.Activities) execution.Activities.Add(pair.Key, pair.Value);
        foreach (var pair in bindings.Execution.Conditions) execution.Conditions.Add(pair.Key, pair.Value);
        var nodes = new Dictionary<int, OdxNode>();
        void Collect(OdxNode node, int depth)
        {
            if (depth > 64 || nodes.Count >= 10000 || !nodes.TryAdd(node.Id, node)) throw new ArgumentException("Invalid or oversized ODX source model.");
            foreach (var child in node.Children) Collect(child, depth + 1);
        }
        foreach (var root in program.Roots) Collect(root, 1);
        void ValidateDurable(OdxNode node)
        {
            if (node.ShapeType is "AtomicTransaction" or "LongRunningTransaction")
                throw new NotSupportedException($"Durable ODX scope {node.ParentId} requires a real transaction implementation; a checkpoint is not a BizTalk transaction.");
            if (node.ShapeType == "Receive")
            {
                if (!bindings.Receives.TryGetValue(node.Id, out var factory) || factory is null) throw new NotSupportedException($"Receive {node.Id} has no correlation/schema binding.");
                execution.Activities[node.Id] = static (_, _, _) => Task.CompletedTask;
                return;
            }
            if (node.ShapeType == "Delay")
            {
                if (!bindings.Deadlines.TryGetValue(node.Id, out var factory) || factory is null) throw new NotSupportedException($"Delay {node.Id} has no pinned deadline binding.");
                execution.Activities[node.Id] = static (_, _, _) => Task.CompletedTask;
                return;
            }
            foreach (var child in node.Children) ValidateDurable(child);
        }
        ValidateDurable(entry);
        new OdxProgramExecutor().ValidateContinuation(program, entry, execution, cancellationToken);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("scoped-correlations-v2\n" + JsonSerializer.Serialize(program, JsonOptions) + "\n" + bindingVersion)));
        var checkpoint = await store.GetAsync(frame.Context.FlowId, correlationKey, cancellationToken).ConfigureAwait(false);
        var persisted = checkpoint is not null;
        OdxDurableState state;
        var instanceId = checkpoint?.InstanceId ?? Execution.OrchestrationInstanceId.Create();
        var receipts = checkpoint?.SourceReceipts.ToList() ?? [];
        if (checkpoint is null)
        {
            state = new() { DefinitionHash = hash, EntryId = entry.Id, Stack = [new() { NodeId = entry.Id }] };
        }
        else
        {
            if (!string.Equals(checkpoint.CorrelationKey, correlationKey, StringComparison.Ordinal))
                throw new InvalidOperationException("Checkpoint correlation identity differs in case. Use distinct encoded instance keys with a case-insensitive store.");
            if (!checkpoint.StateValues.TryGetValue(StateKey, out var json) || json.Length > MaxStateCharacters)
                throw new InvalidOperationException("Checkpoint is not a bounded ODX continuation; no execution was attempted.");
            state = JsonSerializer.Deserialize<OdxDurableState>(json, JsonOptions) ?? throw new InvalidOperationException("Missing ODX continuation.");
            if (state.Version != 1 || state.DefinitionHash != hash || state.EntryId != entry.Id)
                throw new InvalidOperationException("ODX runtime, program or binding version changed while an instance is pending. Restore its pinned runtime and definition before resuming.");
            if (state.EffectNodeId is { } effect) throw new OdxAmbiguousEffectException($"ODX effect {effect} has an unresolved intent. Reconcile its destination outcome; automatic replay is blocked.");
            if (state.Status == "Faulted") throw new InvalidOperationException("ODX instance is faulted and requires reconciliation: " + state.Failure);
            bool ValidCursor(OdxCursor cursor)
            {
                if (cursor is null || !nodes.TryGetValue(cursor.NodeId, out var node) || cursor.Position < 0 || cursor.Iterations is < 0 or > 10000) return false;
                if (cursor.HandlerId is { } handler && (node.ShapeType is not ("Scope" or "ServiceDeclaration") || cursor.Fault is null
                    || !node.Children.Any(child => child.Id == handler && child.ShapeType == "Catch"))) return false;
                var maximum = node.ShapeType switch
                {
                    "Scope" or "ServiceDeclaration" => cursor.HandlerId is { } handlerId ? nodes[handlerId].Children.Count : node.Children.Count(child => child.ShapeType != "Catch"),
                    "ServiceBody" or "Construct" or "DecisionBranch" or "ListenBranch" => node.Children.Count,
                    "While" or "Loop" => node.Children.Count + 1,
                    "Listen" => 1,
                    _ => 0
                };
                return cursor.Position <= maximum;
            }
            bool ValidWaits()
            {
                if (state.Waits.Count == 0) return true;
                if (state.Stack.LastOrDefault() is not { Position: 0 } cursor) return false;
                var owner = nodes[cursor.NodeId];
                (OdxNode Trigger, int? Branch)[] expected = owner.ShapeType == "Listen"
                    ? owner.Children.Select(branch => (branch.Children[0], (int?)branch.Id)).ToArray()
                    : owner.ShapeType is "Receive" or "Delay" ? [(owner, null)] : [];
                if (state.Waits.Count != expected.Length) return false;
                for (var index = 0; index < expected.Length; index++)
                {
                    var wait = state.Waits[index]; var trigger = expected[index].Trigger;
                    if (wait is null || wait.TriggerId != trigger.Id || wait.BranchId != expected[index].Branch || wait.Correlation is null
                        || wait.Correlation.Count > 128 || wait.Correlation.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                        || wait.RequiredMetadata is null || wait.RequiredMetadata.Count > 128 || wait.RequiredMetadata.Any(string.IsNullOrWhiteSpace)
                        || wait.RequiredMetadata.Distinct(StringComparer.Ordinal).Count() != wait.RequiredMetadata.Count
                        || wait.OperationName is not null && string.IsNullOrWhiteSpace(wait.OperationName)
                        || wait.OperationMessageName is not null && string.IsNullOrWhiteSpace(wait.OperationMessageName)) return false;
                    if (trigger.ShapeType == "Receive" ? string.IsNullOrWhiteSpace(wait.Port) || string.IsNullOrWhiteSpace(wait.MessageName) || wait.DeadlineUtc is not null
                        : wait.Port is not null || wait.MessageName is not null || wait.OperationName is not null || wait.OperationMessageName is not null
                            || wait.Correlation.Count != 0 || wait.RequiredMetadata.Count != 0 || wait.DeadlineUtc is null) return false;
                }
                return true;
            }
            if (state.Status is not ("Running" or "Completed") || state.Stack is null || state.Waits is null || state.AcceptedMessages is null
                || state.Variables is null || state.Messages is null || state.Responses is null || state.Stack.Count > 64 || state.Stack.Any(cursor => !ValidCursor(cursor))
                || state.Operations < 0 || state.Operations > 100000 || state.AcceptedMessages.Count > 10000 || state.AcceptedMessages.Any(string.IsNullOrWhiteSpace)
                || !ValidWaits() || state.Messages.Values.Any(value => value is null || value.Any(payload => payload is null)) || state.Responses.Any(payload => payload is null)
                || state.Status == "Completed" && (state.Stack.Count != 0 || state.Waits.Count != 0))
                throw new InvalidOperationException("Invalid persisted ODX continuation.");
            frame.Batch = new(checkpoint.Payloads.Select(payload => payload.Restore()).ToArray());
            frame.Variables.Clear(); foreach (var pair in state.Variables) frame.Variables.Add(pair.Key, pair.Value?.DeepClone());
            frame.Messages.Clear(); foreach (var pair in state.Messages) frame.Messages.Add(pair.Key, new(pair.Value.Select(payload => payload.Restore()).ToArray()));
            frame.Context.CaptureResponse(new(state.Responses.Select(payload => payload.Restore()).ToArray()));
        }
        frame.Context.SetOrchestrationInstance(instanceId, correlationKey, entry.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), checkpoint is not null);
        foreach (var receipt in receipts) frame.Context.RegisterSourceReceipt(receipt);
        if (state.Status == "Completed")
        {
            if (incoming is not null && !state.AcceptedMessages.Contains(incoming.Id)) return new(OdxDurableOutcome.Unmatched, null);
            frame.Context.MarkCheckpointForCompletion(frame.Context.FlowId, correlationKey);
            return new(OdxDurableOutcome.Completed, null);
        }
        var available = incoming is not null && !state.AcceptedMessages.Contains(incoming.Id) ? incoming : null;
        var usedIncoming = false;

        async Task Save()
        {
            // Stage a new activation locally until it accepts input; external-effect intent still requires persistence.
            if (!persisted && incoming is not null && !usedIncoming && state.Status == "Running" && state.EffectNodeId is null) return;
            try
            {
                var payloads = frame.Batch.Payloads.Concat(frame.Messages.Values.SelectMany(batch => batch.Payloads)).Concat(frame.Context.CapturedResponses).ToArray();
                if (payloads.Length > 10000 || payloads.Sum(payload => (long)payload.Content.ToMemory().Length) > 12_000_000)
                    throw new InvalidOperationException("ODX continuation exceeds 10000 payload snapshots or 12,000,000 payload bytes.");
                state.Variables = frame.Variables.ToDictionary(pair => pair.Key, pair => pair.Value?.DeepClone(), StringComparer.Ordinal);
                state.Messages = frame.Messages.ToDictionary(pair => pair.Key, pair => pair.Value.Payloads.Select(FlowOrchestrationPayloadSnapshot.Capture).ToList(), StringComparer.Ordinal);
                state.Responses = frame.Context.CapturedResponses.Select(FlowOrchestrationPayloadSnapshot.Capture).ToList();
                var json = JsonSerializer.Serialize(state, JsonOptions);
                if (json.Length > MaxStateCharacters) throw new InvalidOperationException("ODX continuation exceeds its 24,000,000-character limit.");
                var saved = new FlowOrchestrationCheckpoint
                {
                    FlowId = frame.Context.FlowId, CorrelationKey = correlationKey, InstanceId = instanceId,
                    Bookmark = "odx:" + (state.Stack.LastOrDefault()?.NodeId.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "completed"),
                    UpdatedAt = clock.GetUtcNow(), StateValues = new(StringComparer.OrdinalIgnoreCase) { [StateKey] = json },
                    Payloads = frame.Batch.Payloads.Select(FlowOrchestrationPayloadSnapshot.Capture).ToList(), SourceReceipts = receipts.ToList(),
                    Odx = new()
                    {
                        ModuleId = frame.Step.Module, LastExecutionId = frame.Context.ExecutionId,
                        DeadlineUtc = state.Waits.Count == 0 && state.Status == "Running" && state.EffectNodeId is null ? clock.GetUtcNow()
                            : state.Waits.Where(wait => wait.DeadlineUtc.HasValue).Select(wait => wait.DeadlineUtc).Min(),
                        RecoveryPending = state.Status is "Completed" or "Faulted" || state.EffectNodeId.HasValue
                    }
                };
                if (JsonSerializer.Serialize(saved, JsonOptions).Length > MaxStateCharacters) throw new InvalidOperationException("ODX checkpoint exceeds 24,000,000 characters including payloads and receipts.");
                await store.UpsertAsync(saved, cancellationToken).ConfigureAwait(false);
                persisted = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            { throw new OdxPersistenceException("ODX checkpoint acknowledgement failed; source catch handlers cannot retry an ambiguous persistence boundary.", exception); }
        }
        void Pop()
        {
            var node = nodes[state.Stack[^1].NodeId];
            if (node.ShapeType is "ServiceDeclaration" or "Scope") OdxReceiveBindings.ExitScope(node, frame);
            state.Stack.RemoveAt(state.Stack.Count - 1);
        }
        void Push(OdxNode node) { if (state.Stack.Count >= 64) throw new OdxExecutionLimitException("ODX continuation exceeds 64 stack frames."); state.Stack.Add(new() { NodeId = node.Id }); }
        async Task Accept(OdxWaitChoice choice)
        {
            var message = available!;
            frame.Messages[choice.MessageName!] = message.Batch;
            if (bindings.AcceptedReceives.TryGetValue(choice.TriggerId, out var accepted))
                await accepted(nodes[choice.TriggerId], frame, cancellationToken).ConfigureAwait(false);
            if (state.AcceptedMessages.Count >= 10000) throw new OdxExecutionLimitException("ODX instance exceeds 10000 received messages.");
            state.AcceptedMessages.Add(message.Id);
            foreach (var receipt in message.SourceReceipts)
            {
                if (receipts.All(saved => saved.Id != receipt.Id)) receipts.Add(receipt);
                frame.Context.RegisterSourceReceipt(receipt);
            }
            available = null; usedIncoming = true;
        }
        OdxWaitChoice Choice(OdxNode trigger, int? branchId)
        {
            if (trigger.ShapeType == "Delay") return new() { TriggerId = trigger.Id, BranchId = branchId, DeadlineUtc = bindings.Deadlines[trigger.Id](trigger, frame).ToUniversalTime() };
            var subscription = bindings.Receives[trigger.Id](trigger, frame);
            if (subscription is null || string.IsNullOrWhiteSpace(subscription.Port) || string.IsNullOrWhiteSpace(subscription.MessageName)
                || subscription.Correlation is null || subscription.Correlation.Count > 128 || subscription.Correlation.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Value is null)
                || subscription.RequiredMetadata is null || subscription.RequiredMetadata.Count > 128 || subscription.RequiredMetadata.Any(string.IsNullOrWhiteSpace)
                || subscription.RequiredMetadata.Distinct(StringComparer.Ordinal).Count() != subscription.RequiredMetadata.Count
                || subscription.OperationName is not null && string.IsNullOrWhiteSpace(subscription.OperationName)
                || subscription.OperationMessageName is not null && string.IsNullOrWhiteSpace(subscription.OperationMessageName))
                throw new InvalidOperationException($"Receive {trigger.Id} produced an invalid subscription.");
            return new() { TriggerId = trigger.Id, BranchId = branchId, Port = subscription.Port, MessageName = subscription.MessageName,
                Correlation = new(subscription.Correlation, StringComparer.Ordinal), RequiredMetadata = subscription.RequiredMetadata.ToList(),
                OperationName = subscription.OperationName, OperationMessageName = subscription.OperationMessageName };
        }
        bool Matches(OdxWaitChoice choice) => available is not null && choice.Port == available.Port
            && (choice.OperationName is null || choice.OperationName == available.OperationName)
            && (choice.OperationMessageName is null || choice.OperationMessageName == available.OperationMessageName)
            && choice.Correlation.All(pair => OdxReceiveBindings.TryExactMetadata(available.Batch.Payloads[0], pair.Key, out var value) && value == pair.Value)
            && choice.RequiredMetadata.All(key => OdxReceiveBindings.TryExactMetadata(available.Batch.Payloads[0], key, out _))
            && (!bindings.ReceiveFilters.TryGetValue(choice.TriggerId, out var filter) || filter(available.Batch.Payloads[0]));
        async Task<OdxDurableResult?> Wait(OdxNode node, OdxCursor cursor)
        {
            if (state.Waits.Count == 0)
            {
                state.Waits = node.ShapeType == "Listen" ? node.Children.Select(branch => Choice(branch.Children[0], branch.Id)).ToList() : [Choice(node, null)];
                await Save().ConfigureAwait(false);
            }
            // A due persisted timer wins over a later arrival; receive branches otherwise retain source priority.
            var winner = state.Waits.Where(choice => choice.DeadlineUtc <= clock.GetUtcNow()).OrderBy(choice => choice.DeadlineUtc).FirstOrDefault()
                ?? state.Waits.FirstOrDefault(Matches);
            if (winner is null)
            {
                var deadline = state.Waits.Where(choice => choice.DeadlineUtc.HasValue).Select(choice => choice.DeadlineUtc).Min();
                if (available is not null) return new(OdxDurableOutcome.Unmatched, deadline);
                frame.Context.Suspend("Waiting at persisted ODX receive/listen/delay " + node.Id);
                return new(OdxDurableOutcome.Waiting, deadline);
            }
            if (winner.Port is null && available is not null) return new(OdxDurableOutcome.Unmatched, winner.DeadlineUtc);
            if (winner.Port is not null) await Accept(winner).ConfigureAwait(false);
            state.Waits.Clear();
            if (winner.BranchId is { } branch)
            {
                cursor.Position = 1;
                Push(nodes[branch]); state.Stack[^1].Position = 1;
            }
            else Pop();
            await Save().ConfigureAwait(false);
            return null;
        }
        bool Handle(Exception exception)
        {
            for (var index = state.Stack.Count - 1; index >= 0; index--)
            {
                var cursor = state.Stack[index]; var scope = nodes[cursor.NodeId];
                if (scope.ShapeType is not ("Scope" or "ServiceDeclaration") || cursor.HandlerId is not null) continue;
                var handler = scope.Children.FirstOrDefault(child => child.ShapeType == "Catch" && Inherits(exception, child.Properties.GetValueOrDefault("ExceptionType")));
                if (handler is null) continue;
                while (state.Stack.Count > index + 1) Pop();
                cursor.HandlerId = handler.Id; cursor.Position = 0;
                cursor.Fault = new(exception.GetType().FullName ?? exception.GetType().Name, exception.Message, exception.StackTrace);
                state.Waits.Clear();
                return true;
            }
            return false;
        }
        while (state.Stack.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++state.Operations > 100000) throw new OdxExecutionLimitException("ODX instance exceeded 100000 control-flow operations.");
            var cursor = state.Stack[^1]; var node = nodes[cursor.NodeId];
            var fault = state.Stack.LastOrDefault(item => item.Fault is not null)?.Fault;
            frame.CurrentException = fault is null ? null : new OdxRestoredException(fault);
            try
            {
                switch (node.ShapeType)
                {
                    case "ServiceDeclaration": case "Scope":
                        var children = cursor.HandlerId is { } handler ? nodes[handler].Children : OdxProgramExecutor.ScopeChildren(node);
                        if (cursor.Position >= children.Count) Pop(); else Push(children[cursor.Position++]);
                        break;
                    case "ServiceBody": case "Construct": case "DecisionBranch": case "ListenBranch":
                        if (cursor.Position >= node.Children.Count) Pop(); else Push(node.Children[cursor.Position++]);
                        break;
                    case "Decision":
                        Pop();
                        foreach (var branch in node.Children)
                            if (string.IsNullOrWhiteSpace(branch.Properties.GetValueOrDefault("Expression")) || await execution.Conditions[branch.Id](branch, frame, cancellationToken).ConfigureAwait(false)) { Push(branch); break; }
                        break;
                    case "While": case "Loop":
                        if (cursor.Position == 0)
                        {
                            if (!await execution.Conditions[node.Id](node, frame, cancellationToken).ConfigureAwait(false)) { Pop(); break; }
                            if (++cursor.Iterations > 10000) throw new OdxExecutionLimitException($"ODX loop {node.Id} exceeded 10000 iterations.");
                            cursor.Position = 1;
                        }
                        if (cursor.Position <= node.Children.Count) Push(node.Children[cursor.Position++ - 1]);
                        else cursor.Position = 0;
                        break;
                    case "Listen" when cursor.Position > 0:
                        Pop(); break;
                    case "Receive": case "Delay": case "Listen":
                        var waitResult = await Wait(node, cursor).ConfigureAwait(false);
                        if (waitResult is not null) return waitResult;
                        break;
                    default:
                        if (node.ShapeType == "Send" && bindings.PreparingSends.TryGetValue(node.Id, out var prepare))
                            await prepare(node, frame, cancellationToken).ConfigureAwait(false);
                        var external = !bindings.FrameLocalActivities.Contains(node.Id);
                        if (external) { state.EffectNodeId = node.Id; await Save().ConfigureAwait(false); }
                        try { await execution.Activities[node.Id](node, frame, cancellationToken).ConfigureAwait(false); }
                        catch (Exception exception) when (external)
                        { throw new OdxAmbiguousEffectException($"ODX activity {node.Id} failed after effect intent was saved. Its external outcome must be reconciled before replay.", exception); }
                        if (frame.Context.Disposition != FlowExecutionDisposition.Continue) throw new InvalidOperationException("Durable activities must use explicit receive/delay bindings, not context suspension.");
                        Pop(); state.EffectNodeId = null;
                        await Save().ConfigureAwait(false);
                        break;
                }
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OdxExecutionLimitException or OdxAmbiguousEffectException or OdxPersistenceException) && state.EffectNodeId is null)
            {
                if (!Handle(exception)) { state.Status = "Faulted"; state.Failure = exception.GetType().FullName + ": " + exception.Message; await Save().ConfigureAwait(false); throw; }
                await Save().ConfigureAwait(false);
            }
        }
        if (available is not null && !usedIncoming) return new(OdxDurableOutcome.Unmatched, null);
        state.Status = "Completed"; frame.CurrentException = null;
        await Save().ConfigureAwait(false);
        frame.Context.MarkCheckpointForCompletion(frame.Context.FlowId, correlationKey);
        return new(OdxDurableOutcome.Completed, null);
    }

    private static bool Inherits(Exception exception, string? typeName)
    {
        for (var type = exception.GetType(); type is not null; type = type.BaseType) if (type.FullName == typeName) return true;
        return false;
    }
}
