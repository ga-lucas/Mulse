using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mulse.Modules.Decisions;

namespace Mulse.Modules.ControlFlow;

/// <summary>A persisted native continuation. The caller must serialize executions for each flow/instance.</summary>
public sealed class NativeDurableExecutor(IFlowOrchestrationStateStore store, TimeProvider clock,
    IAugmentModuleInvoker augments, IRenderModuleInvoker renderers, IDeliverModuleInvoker? deliveries = null)
{
    private const int MaxCharacters = 24_000_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 128 };

    public static string CorrelationKey(string value) => "native:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public async Task<IntegrationBatch> ExecuteAsync(NativeFlowDefinition definition, FlowExecutionContext context,
        IntegrationBatch incoming, ModuleStepDefinition step, CancellationToken cancellationToken)
    {
        definition = NativeFlowDefinition.Read(JsonSerializer.Serialize(definition, Json));
        if (!definition.Durable) throw new ArgumentException("Native durable execution requires durable=true.");
        if (context.Disposition != FlowExecutionDisposition.Continue) throw new InvalidOperationException("Native resume requires a fresh execution context.");
        if (incoming.Count > 1) throw new ArgumentException("Native durable activation requires exactly one incoming message or an empty timer resume.");
        if (definition.AllNodes().Any(node => node.Kind == NativeFlowNodeKind.Deliver) && deliveries is null)
            throw new NotSupportedException("Native Deliver requires a leased delivery invoker.");
        string? incomingId = null;
        var key = context.OrchestrationCorrelationKey;
        if (incoming.Count == 1)
        {
            var payload = incoming.Payloads[0];
            var group = RequiredMetadata(payload, definition.CorrelationMetadataKey);
            incomingId = RequiredMetadata(payload, definition.MessageIdMetadataKey);
            var inputKey = CorrelationKey(group);
            if (key is not null && key != inputKey) throw new NativeUnmatchedMessageException("Incoming native correlation differs from the resumed instance.");
            key = inputKey;
        }
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Native timer resume requires its persisted correlation key.");
        var hash = DefinitionHash(definition);
        var stored = await store.GetAsync(context.FlowId, key, cancellationToken).ConfigureAwait(false);
        // Some stores retain object references. Never mutate their last acknowledged checkpoint in place.
        var checkpoint = stored is null ? null : Clone(stored);
        var nodes = definition.AllNodes().ToDictionary(node => node.Id, StringComparer.Ordinal);
        var state = checkpoint?.Native?.State ?? new NativeDurableState
        {
            DefinitionHash = hash, Stack = [new()], InitialMetadata = incoming.Count == 1
                ? incoming.Payloads[0].Metadata.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) : new(StringComparer.Ordinal)
        };
        var instance = checkpoint?.InstanceId ?? Guid.NewGuid().ToString("N");
        var receipts = checkpoint?.SourceReceipts.ToList() ?? [];
        var batch = checkpoint is null ? incoming : Restore(checkpoint.Payloads);
        var persisted = checkpoint is not null;
        var available = incoming.Count == 1;
        var activated = checkpoint is not null;
        if (checkpoint is not null)
        {
            if (checkpoint.CorrelationKey != key || checkpoint.Native is null || checkpoint.Native.ModuleId != step.Module
                || checkpoint.Odx is not null || checkpoint.Batch is not null || state.DefinitionHash != hash || state.Version != 1)
                throw new InvalidOperationException("Native checkpoint runtime, module or definition changed. Restore the pinned definition; no reset or replay was attempted.");
            ValidateState(state, definition, nodes, checkpoint.Payloads);
            if (state.EffectNodeId is not null) throw new NativeAmbiguousEffectException($"Native effect '{state.EffectNodeId}' has unresolved persisted intent. Reconcile the destination; blind replay is blocked.");
            if (state.Status == "Faulted") throw new InvalidOperationException("Native instance is faulted and requires explicit reconciliation: " + state.Failure);
            if (incomingId is not null && state.AcceptedMessageIds.Contains(incomingId, StringComparer.Ordinal))
                throw new NativeUnmatchedMessageException("Duplicate native message ID was not accepted or acknowledged.");
            if (available)
            {
                if (state.Status == "Completed" || state.Stack.LastOrDefault() is not { } waiting
                    || !nodes.TryGetValue(waiting.NodeId, out var receive) || receive.Kind != NativeFlowNodeKind.Receive
                    || !ReceiveMatches(receive, waiting))
                    throw new NativeUnmatchedMessageException("Incoming native message does not match the persisted receive/channel/correlation/filter/deadline; it was not accepted or acknowledged.");
            }
        }
        else
        {
            if (!available) throw new InvalidOperationException("Native timer resume has no checkpoint; a new instance cannot be silently created.");
            foreach (var correlation in definition.AllNodes().SelectMany(node => node.CorrelationKeys).Distinct(StringComparer.Ordinal))
                if (!state.InitialMetadata.TryGetValue(correlation, out var value) || value is null || value.Length > 2048)
                    throw new ArgumentException("Native input requires initialized bounded receive correlation metadata: " + correlation);
        }
        context.SetOrchestrationInstance(instance, key, checkpoint?.Bookmark ?? "native:start", persisted);
        foreach (var receipt in receipts) context.RegisterSourceReceipt(receipt);
        context.CaptureResponse(Restore(state.Responses));

        bool ReceiveMatches(NativeFlowNode node, NativeDurableCursor cursor)
        {
            if (!available || cursor.DeadlineUtc is { } deadline && clock.GetUtcNow() >= deadline) return false;
            var payload = incoming.Payloads[0];
            if (!TryMetadata(payload, "channel", out var channel) || channel != node.Channel) return false;
            foreach (var correlation in node.CorrelationKeys)
                if (!state.InitialMetadata.TryGetValue(correlation, out var pinned) || !TryMetadata(payload, correlation, out var value) || pinned != value) return false;
            return DecisionRuntime.MatchesAll(payload, node.Conditions, node.Id);
        }
        void Accept()
        {
            if (!available) return;
            if (state.AcceptedMessageIds.Count >= 10000) throw new InvalidOperationException("Native continuation exceeds 10000 accepted messages.");
            state.AcceptedMessageIds.Add(incomingId!);
            foreach (var receipt in context.SourceReceipts)
                if (receipts.All(saved => saved.Id != receipt.Id)) receipts.Add(receipt);
            available = false; activated = true;
        }
        async Task Save(CancellationToken token)
        {
            if (!activated) return;
            try
            {
                state.Responses = Capture(new(context.CapturedResponses));
                var snapshots = Capture(batch);
                ValidateSize(state, snapshots);
                var top = state.Stack.LastOrDefault();
                var saved = new FlowOrchestrationCheckpoint
                {
                    FlowId = context.FlowId, CorrelationKey = key, InstanceId = instance,
                    Bookmark = "native:" + (top?.NodeId is { Length: > 0 } id ? id : state.Status == "Completed" ? "completed" : "start"),
                    UpdatedAt = clock.GetUtcNow(), Payloads = snapshots, SourceReceipts = receipts.ToList(),
                    Native = new()
                    {
                        ModuleId = step.Module, LastExecutionId = context.ExecutionId,
                        DeadlineUtc = state.Status == "Running" && state.EffectNodeId is null
                            ? top?.DeadlineUtc ?? (top is not null && nodes.TryGetValue(top.NodeId, out var pending) && pending.Kind == NativeFlowNodeKind.Receive ? null : clock.GetUtcNow()) : null,
                        RecoveryPending = state.Status is "Completed" or "Faulted" || state.EffectNodeId is not null,
                        State = state
                    }
                };
                var json = JsonSerializer.Serialize(saved, Json);
                if (json.Length > MaxCharacters) throw new InvalidOperationException("Native checkpoint exceeds 24,000,000 characters.");
                await store.UpsertAsync(JsonSerializer.Deserialize<FlowOrchestrationCheckpoint>(json, Json)!, token).ConfigureAwait(false);
                persisted = true;
                context.SetOrchestrationInstance(instance, key, saved.Bookmark, checkpoint is not null);
            }
            catch (Exception exception) when (exception is not NativePersistenceException)
            { throw new NativePersistenceException("Native checkpoint persistence was not acknowledged; do not retry an ambiguous boundary automatically.", exception); }
        }
        void Pop() => state.Stack.RemoveAt(state.Stack.Count - 1);
        void Push(NativeFlowNode node)
        {
            if (state.Stack.Count >= 34) throw new InvalidOperationException("Native continuation exceeds its stack limit.");
            state.Stack.Add(new() { NodeId = node.Id });
        }
        NativeScalar Fact(NativeMessageFact fact) => fact.Read(fact.MessageName.Length == 0 ? batch : Message(fact.MessageName));
        bool Matches(NativeFlowNode node)
        {
            if (node.Expression is not null) return NativeExpression.Boolean(node.Expression.Evaluate(state.Variables, Fact).Read());
            if (batch.Count != 1) throw new InvalidOperationException($"Native condition '{node.Id}' requires exactly one payload.");
            return DecisionRuntime.MatchesAll(batch.Payloads[0], node.Conditions, node.Id);
        }
        IntegrationBatch Message(string name) => state.Messages.TryGetValue(name, out var message)
            ? Restore(message) : throw new InvalidOperationException("Native named message is uninitialized: " + name);
        async Task<IntegrationBatch> Complete()
        {
            var output = definition.OutputMessage.Length == 0 ? batch : Message(definition.OutputMessage);
            if (state.Status != "Completed")
            {
                state.Status = "Completed";
                await Save(cancellationToken).ConfigureAwait(false);
            }
            context.MarkCheckpointForCompletion(context.FlowId, key);
            return output;
        }
        if (state.Status == "Completed") return await Complete().ConfigureAwait(false);
        try
        {
            while (state.Stack.Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.Operations >= 100000) throw new InvalidOperationException("Native control flow exceeded 100000 node transitions.");
                state.Operations++;
                var cursor = state.Stack[^1];
                var node = cursor.NodeId.Length == 0 ? null : nodes[cursor.NodeId];
                if (node is null || node.Kind == NativeFlowNodeKind.Sequence)
                {
                    var children = node?.Children ?? definition.Nodes;
                    if (cursor.Position == children.Count) Pop(); else Push(children[cursor.Position++]);
                }
                else if (node.Kind == NativeFlowNodeKind.If)
                {
                    if (cursor.Phase == 0) cursor.Phase = Matches(node) ? 1 : 2;
                    var children = cursor.Phase == 1 ? node.Children : node.Otherwise;
                    if (cursor.Position == children.Count) Pop(); else Push(children[cursor.Position++]);
                }
                else if (node.Kind == NativeFlowNodeKind.While)
                {
                    if (cursor.Phase == 0)
                    {
                        if (!Matches(node)) Pop();
                        else
                        {
                            if (cursor.Iterations >= node.MaxIterations) throw new InvalidOperationException($"Native loop '{node.Id}' exceeded its iteration limit.");
                            cursor.Iterations++;
                            cursor.Phase = 1; cursor.Position = 0;
                        }
                    }
                    else if (cursor.Position == node.Children.Count) cursor.Phase = 0;
                    else Push(node.Children[cursor.Position++]);
                }
                else if (node.Kind == NativeFlowNodeKind.ForEach)
                {
                    if (cursor.Phase == 0)
                    {
                        if (batch.Count > node.MaxIterations) throw new InvalidOperationException($"Native loop '{node.Id}' exceeds its payload iteration limit.");
                        cursor.Inputs = Capture(batch); cursor.Phase = 1;
                        if (cursor.Inputs.Count != 0) batch = Restore([cursor.Inputs[0]]);
                    }
                    else if (cursor.Iterations == cursor.Inputs.Count)
                    { batch = Restore(cursor.Outputs); Pop(); }
                    else if (cursor.Position == node.Children.Count)
                    {
                        cursor.Outputs.AddRange(Capture(batch)); cursor.Iterations++; cursor.Position = 0;
                        if (cursor.Iterations < cursor.Inputs.Count) batch = Restore([cursor.Inputs[cursor.Iterations]]);
                    }
                    else Push(node.Children[cursor.Position++]);
                }
                else if (node.Kind == NativeFlowNodeKind.Receive)
                {
                    if (cursor.Phase == 0)
                    {
                        foreach (var correlation in node.CorrelationKeys)
                            if (!state.InitialMetadata.ContainsKey(correlation)) throw new InvalidOperationException("Native receive correlation was not initialized: " + correlation);
                        cursor.Phase = 1;
                        cursor.DeadlineUtc = node.DurationSeconds == 0 ? null : clock.GetUtcNow().AddSeconds(node.DurationSeconds);
                    }
                    if (available)
                    {
                        if (!ReceiveMatches(node, cursor)) throw new NativeUnmatchedMessageException("Initial native message does not match its receive; it was not accepted or acknowledged.");
                        batch = incoming; state.Messages[node.MessageName] = Capture(incoming); Accept(); Pop();
                    }
                    else if (cursor.DeadlineUtc is { } deadline && clock.GetUtcNow() >= deadline)
                        throw new TimeoutException($"Native receive '{node.Id}' timed out; explicit reconciliation is required.");
                    else
                    {
                        await Save(cancellationToken).ConfigureAwait(false);
                        context.Suspend("Native receive: " + node.Id); return batch;
                    }
                }
                else if (node.Kind == NativeFlowNodeKind.Wait)
                {
                    Accept();
                    if (cursor.Phase == 0) { cursor.Phase = 1; cursor.DeadlineUtc = clock.GetUtcNow().AddSeconds(node.DurationSeconds); }
                    if (clock.GetUtcNow() < cursor.DeadlineUtc)
                    {
                        await Save(cancellationToken).ConfigureAwait(false);
                        context.Suspend("Native wait: " + node.Id); return batch;
                    }
                    Pop();
                }
                else
                {
                    if (node.Kind is not (NativeFlowNodeKind.DeclareVariable or NativeFlowNodeKind.SetVariable or NativeFlowNodeKind.CreateXmlMessage or NativeFlowNodeKind.SetMessageValue or NativeFlowNodeKind.SaveMessage or NativeFlowNodeKind.UseMessage or NativeFlowNodeKind.CreateXmlRoot or NativeFlowNodeKind.AppendXmlMessage)) Accept();
                    switch (node.Kind)
                    {
                        case NativeFlowNodeKind.DeclareVariable:
                        case NativeFlowNodeKind.SetVariable: NativeVariables.Apply(node, state.Variables, state.VariableTypes, Fact); Pop(); break;
                        case NativeFlowNodeKind.CreateXmlMessage:
                            state.Messages[node.MessageName] = Capture(NativeMessageOperations.CreateXml(node.MessageName, node.Expression!.Evaluate(state.Variables, Fact))); Pop(); break;
                        case NativeFlowNodeKind.CreateXmlRoot:
                            state.Messages[node.MessageName] = Capture(NativeMessageOperations.CreateRoot(node.MessageName, node.Expression!.Evaluate(state.Variables, Fact), node.NamespaceExpression!.Evaluate(state.Variables, Fact))); Pop(); break;
                        case NativeFlowNodeKind.AppendXmlMessage:
                            state.Messages[node.MessageName] = Capture(NativeMessageOperations.AppendXml(Message(node.MessageName), Message(node.SourceMessage!))); Pop(); break;
                        case NativeFlowNodeKind.SetMessageValue:
                            var target = node.Target!;
                            state.Messages[target.MessageName] = Capture(NativeMessageOperations.SetValue(target, node.Expression!.Evaluate(state.Variables, Fact), Message(target.MessageName))); Pop(); break;
                        case NativeFlowNodeKind.SaveMessage: state.Messages[node.MessageName] = Capture(batch); Pop(); break;
                        case NativeFlowNodeKind.UseMessage: batch = Message(node.MessageName); Pop(); break;
                        case NativeFlowNodeKind.Reply: context.CaptureResponse(Message(node.MessageName)); Pop(); break;
                        case NativeFlowNodeKind.Augment:
                        case NativeFlowNodeKind.Render:
                        case NativeFlowNodeKind.Deliver:
                            var effectBatch = node.Kind == NativeFlowNodeKind.Deliver && node.MessageName.Length != 0 ? Message(node.MessageName) : batch;
                            state.EffectNodeId = node.Id; state.EffectKind = node.Kind; state.EffectModule = node.Module;
                            await Save(cancellationToken).ConfigureAwait(false);
                            var result = node.Kind switch
                            {
                                NativeFlowNodeKind.Augment => await augments.AugmentAsync(node.Module, context, batch, node.Settings, cancellationToken).ConfigureAwait(false),
                                NativeFlowNodeKind.Render => await renderers.RenderAsync(node.Module, context, batch, node.Settings, cancellationToken).ConfigureAwait(false),
                                _ => await deliveries!.DeliverAsync(node.Module, context, effectBatch, node.Settings, cancellationToken).ConfigureAwait(false)
                            };
                            if (context.Disposition != FlowExecutionDisposition.Continue || context.DeliveryFailures.Count != 0)
                                throw new InvalidOperationException($"Native effect '{node.Id}' suspended or reported delivery failure; its intent requires reconciliation.");
                            if (node.Kind == NativeFlowNodeKind.Deliver)
                            { if (result.Count != 0) context.CaptureResponse(result); }
                            else batch = result;
                            state.EffectNodeId = null; state.EffectKind = null; state.EffectModule = null; Pop();
                            // Once an effect has returned, cancellation must not prevent recording its outcome.
                            await Save(CancellationToken.None).ConfigureAwait(false);
                            continue;
                        default: throw new NotSupportedException($"Native node '{node.Id}' is not executable; transactions are unsupported.");
                    }
                }
                await Save(cancellationToken).ConfigureAwait(false);
            }
            Accept();
            return await Complete().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not NativePersistenceException && exception is not NativeUnmatchedMessageException)
        {
            state.Status = "Faulted"; state.Failure = exception.GetType().Name + ": " + exception.Message;
            if (activated) await Save(CancellationToken.None).ConfigureAwait(false);
            if (state.EffectNodeId is not null)
                throw new NativeAmbiguousEffectException($"Native effect '{state.EffectNodeId}' did not complete safely: {state.Failure}. Reconcile before any replay.", exception);
            throw;
        }
    }

    private static List<FlowOrchestrationPayloadSnapshot> Capture(IntegrationBatch batch) => batch.Payloads.Select(payload => FlowOrchestrationPayloadSnapshot.Capture(payload, preserveMetadataCase: true)).ToList();
    private static IntegrationBatch Restore(IEnumerable<FlowOrchestrationPayloadSnapshot> snapshots) => new(snapshots.Select(snapshot => snapshot.Restore()).ToArray());
    internal static string DefinitionHash(NativeFlowDefinition definition) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("native-durable-v1\n" + JsonSerializer.Serialize(definition, Json))));

    internal static FlowOrchestrationCheckpoint Clone(FlowOrchestrationCheckpoint checkpoint)
    {
        var json = JsonSerializer.Serialize(checkpoint, Json);
        if (json.Length > MaxCharacters) throw new InvalidOperationException("Persisted native checkpoint exceeds its size limit.");
        return JsonSerializer.Deserialize<FlowOrchestrationCheckpoint>(json, Json)!;
    }
    private static bool TryMetadata(IntegrationPayload payload, string key, out string value)
    {
        foreach (var pair in payload.Metadata)
            if (string.Equals(pair.Key, key, StringComparison.Ordinal)) { value = pair.Value; return true; }
        value = ""; return false;
    }
    private static string RequiredMetadata(IntegrationPayload payload, string key) => TryMetadata(payload, key, out var value)
        && !string.IsNullOrWhiteSpace(value) && value.Length <= 2048 ? value : throw new ArgumentException("Native input requires exact bounded metadata: " + key);

    private static void ValidateSize(NativeDurableState state, List<FlowOrchestrationPayloadSnapshot> working)
    {
        var all = working.Concat(state.Messages.Values.SelectMany(value => value)).Concat(state.Responses)
            .Concat(state.Stack.SelectMany(cursor => cursor.Inputs.Concat(cursor.Outputs))).ToArray();
        if (all.Length > 10000 || all.Any(snapshot => snapshot is null || snapshot.Metadata is null || snapshot.ContentBase64 is null)
            || all.Sum(snapshot => (long)snapshot.ContentBase64.Length) > 16_000_000
            || JsonSerializer.Serialize(state, Json).Length > MaxCharacters)
            throw new InvalidOperationException("Native continuation exceeds snapshot/state bounds.");
        foreach (var snapshot in all) _ = snapshot.Restore();
    }
    internal static void ValidateState(NativeDurableState state, NativeFlowDefinition definition,
        Dictionary<string, NativeFlowNode> nodes, List<FlowOrchestrationPayloadSnapshot> working)
    {
        if (state.Status is not ("Running" or "Completed" or "Faulted") || state.Stack is null || state.Stack.Count > 34
            || state.AcceptedMessageIds is null || state.AcceptedMessageIds.Count is < 1 or > 10000
            || state.AcceptedMessageIds.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 2048)
            || state.AcceptedMessageIds.Distinct(StringComparer.Ordinal).Count() != state.AcceptedMessageIds.Count
            || state.InitialMetadata is null || state.Messages is null || state.Responses is null
            || state.CommittedEffects is null || state.CommittedEffects.Count > 10000
            || state.CommittedEffects.Any(effect => effect is null || string.IsNullOrWhiteSpace(effect.NodeId) || !nodes.ContainsKey(effect.NodeId)
                || effect.Operation is < 1 or > 100000 || string.IsNullOrWhiteSpace(effect.OperatorEvidence) || effect.OperatorEvidence.Length > 4096)
            || state.Messages.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 || pair.Value is null)
            || state.Operations is < 0 or > 100000 || state.Status == "Completed" && (state.Stack.Count != 0 || state.EffectNodeId is not null)
            || state.Status == "Running" && state.Stack.Count == 0 && state.EffectNodeId is not null)
            throw new InvalidOperationException("Invalid persisted native continuation; no reset was attempted.");
        if (state.Variables is null || state.VariableTypes is null || state.Variables.Count > 128 || state.Variables.Count != state.VariableTypes.Count)
            throw new InvalidOperationException("Invalid persisted native scalar variables.");
        foreach (var pair in state.Variables)
        {
            if (!NativeExpression.ValidName(pair.Key) || pair.Value is null || !state.VariableTypes.TryGetValue(pair.Key, out var type)
                || !Enum.IsDefined(type) || type == NativeScalarType.Null || pair.Value.Type != type && !(type == NativeScalarType.String && pair.Value.Type == NativeScalarType.Null))
                throw new InvalidOperationException("Invalid persisted native scalar variable type.");
            _ = pair.Value.Read();
        }
        for (var index = 0; index < state.Stack.Count; index++)
        {
            var cursor = state.Stack[index];
            if (cursor is null || cursor.NodeId is null || cursor.Inputs is null || cursor.Outputs is null
                || cursor.Position < 0 || cursor.Phase is < 0 or > 2 || cursor.Iterations is < 0 or > 10000)
                throw new InvalidOperationException("Invalid persisted native cursor.");
            var node = cursor.NodeId.Length == 0 ? null : nodes.GetValueOrDefault(cursor.NodeId);
            if (index == 0 ? cursor.NodeId.Length != 0 : node is null) throw new InvalidOperationException("Invalid native cursor node identity.");
            IReadOnlyList<NativeFlowNode> children = node is null ? definition.Nodes : node.Kind == NativeFlowNodeKind.If && cursor.Phase == 2 ? node.Otherwise : node.Children;
            if (cursor.Position > children.Count || node is null && (cursor.Phase != 0 || cursor.Iterations != 0)
                || node?.Kind == NativeFlowNodeKind.Sequence && (cursor.Phase != 0 || cursor.Iterations != 0)
                || node?.Kind == NativeFlowNodeKind.If && (cursor.Iterations != 0 || cursor.Phase == 0 && cursor.Position != 0)
                || node?.Kind == NativeFlowNodeKind.ForEach && (cursor.Phase > 1 || cursor.Inputs.Count > node.MaxIterations || cursor.Iterations > cursor.Inputs.Count
                    || cursor.Phase == 0 && (cursor.Position != 0 || cursor.Iterations != 0 || cursor.Inputs.Count != 0 || cursor.Outputs.Count != 0))
                || node is not null && node.Kind is not (NativeFlowNodeKind.Sequence or NativeFlowNodeKind.If or NativeFlowNodeKind.ForEach or NativeFlowNodeKind.While or NativeFlowNodeKind.Wait or NativeFlowNodeKind.Receive)
                    && (cursor.Position != 0 || cursor.Phase != 0 || cursor.Iterations != 0)
                || node?.Kind == NativeFlowNodeKind.While && (cursor.Phase > 1 || cursor.Iterations > node.MaxIterations)
                || node?.Kind is NativeFlowNodeKind.Wait or NativeFlowNodeKind.Receive && (cursor.Phase > 1 || cursor.Position != 0 || cursor.Iterations != 0)
                || node?.Kind == NativeFlowNodeKind.Wait && cursor.Phase == 1 && cursor.DeadlineUtc is null
                || node?.Kind == NativeFlowNodeKind.Receive && cursor.Phase == 1 && (node.DurationSeconds == 0 ? cursor.DeadlineUtc is not null : cursor.DeadlineUtc is null)
                || node?.Kind is not (NativeFlowNodeKind.Wait or NativeFlowNodeKind.Receive) && cursor.DeadlineUtc is not null
                || node?.Kind != NativeFlowNodeKind.ForEach && (cursor.Inputs.Count != 0 || cursor.Outputs.Count != 0))
                throw new InvalidOperationException("Invalid persisted native cursor state.");
            if (index + 1 < state.Stack.Count && (cursor.Position == 0 || children[cursor.Position - 1].Id != state.Stack[index + 1].NodeId))
                throw new InvalidOperationException("Invalid persisted native cursor ancestry.");
        }
        if (state.EffectNodeId is null && (state.EffectKind is not null || state.EffectModule is not null))
            throw new InvalidOperationException("Invalid inactive native effect summary.");
        if (state.EffectNodeId is not null && (state.Stack.LastOrDefault()?.NodeId != state.EffectNodeId
            || !nodes.TryGetValue(state.EffectNodeId, out var effect) || effect.Kind is not (NativeFlowNodeKind.Augment or NativeFlowNodeKind.Render or NativeFlowNodeKind.Deliver)
            || state.EffectKind is not null && state.EffectKind != effect.Kind || state.EffectModule is not null && state.EffectModule != effect.Module))
            throw new InvalidOperationException("Invalid persisted native effect intent.");
        ValidateSize(state, working);
    }
}
