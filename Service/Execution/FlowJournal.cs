using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Mulse.Persistence;

namespace Service.Execution;

public sealed class FlowHistoryOptions
{
    public bool CaptureStagePayloads { get; set; }
    public int RetentionDays { get; set; } = 30;
    public int MaxPendingMessages { get; set; } = 10000;
}

public sealed class FlowJournal : IDurableInbox
{
    private readonly WorkspaceDatabase database;
    private readonly IRuntimeConfigurationStore store;
    private readonly Microsoft.Extensions.Options.IOptions<FlowHistoryOptions> options;
    private readonly TimeProvider timeProvider;

    public FlowJournal(WorkspaceDatabase database, IRuntimeConfigurationStore store,
        Microsoft.Extensions.Options.IOptions<FlowHistoryOptions> options, TimeProvider timeProvider)
    {
        _ = store.GetState();
        this.store = store;
        this.database = database;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    public void EnsureAvailable() => database.EnsureAvailable();

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private long Now => timeProvider.GetUtcNow().UtcTicks;
    private static string Key(string executionId) => WorkspaceDocument.Identity(executionId);
    private static string MessageIdentity(Guid id) => "message:" + id.ToString("N");
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static List<FlowOrchestrationPayloadSnapshot> Snapshot(IntegrationBatch batch)
        => batch.Payloads.Select(FlowOrchestrationPayloadSnapshot.Capture).ToList();

    private async Task<T> UseAsync<T>(Func<OperationalSession, Task<T>> action, bool write, CancellationToken cancellationToken)
    {
        try { return await database.OperationsAsync(action, write, cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is not (FlowDurabilityException or KeyNotFoundException or ArgumentException or AdmissionRejectedException))
        {
            throw new FlowDurabilityException("Execution storage failed. Processing is held; inspect persisted state before replaying external effects.", exception);
        }
    }

    public Task EnqueueAsync(string channel, string? flowId, Guid messageId, IntegrationBatch batch, CancellationToken cancellationToken)
        => EnqueueCoreAsync(channel, flowId, messageId, batch, false, cancellationToken);

    public Task EnqueueConfirmedAsync(string channel, Guid messageId, IntegrationBatch batch, CancellationToken cancellationToken)
        => EnqueueCoreAsync(channel, null, messageId, batch, true, cancellationToken);

    private Task EnqueueCoreAsync(string channel, string? flowId, Guid messageId, IntegrationBatch batch, bool retainForConfirmation, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var existing = await session.Db.Messages.SingleOrDefaultAsync(row => row.Id == messageId, cancellationToken).ConfigureAwait(false);
            var serialized = Serialize(Snapshot(batch));
            if (existing is not null)
            {
                if (existing.RetainForConfirmation != retainForConfirmation
                    || existing.ChannelKey != WorkspaceDocument.Identity(channel) || (existing.FlowId != flowId
                    && !(flowId is null && channel.StartsWith("flow-link:", StringComparison.OrdinalIgnoreCase)))
                    || session.Decode(existing.Payload, MessageIdentity(messageId)) != serialized)
                    throw new FlowDurabilityException("The message identity was already used with different content or ownership; no duplicate was queued.");
                return true;
            }
            if (await session.Db.Messages.CountAsync(row => row.State == "Pending" || row.State == "Claimed" || row.State == "Held", cancellationToken).ConfigureAwait(false) >= options.Value.MaxPendingMessages)
                throw new AdmissionRejectedException("Durable inbox capacity reached; retry later.");
            session.Db.Messages.Add(new()
            {
                RetainForConfirmation = retainForConfirmation,
                Id = messageId, ChannelKey = WorkspaceDocument.Identity(channel), Channel = channel, FlowId = flowId,
                CreatedUtcTicks = Now, UpdatedUtcTicks = Now,
                Payload = session.Encode(serialized, MessageIdentity(messageId))
            });
            return true;
        }, true, cancellationToken);

    public Task<bool> ContainsAsync(string channel, Guid messageId, IntegrationBatch batch, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var existing = await session.Db.Messages.SingleOrDefaultAsync(row => row.Id == messageId, cancellationToken).ConfigureAwait(false);
            if (existing is null) return false;
            if (!existing.RetainForConfirmation || existing.ChannelKey != WorkspaceDocument.Identity(channel)
                || session.Decode(existing.Payload, MessageIdentity(messageId)) != Serialize(Snapshot(batch)))
                throw new FlowDurabilityException("The message identity belongs to different content or channel.");
            return true;
        }, false, cancellationToken);

    public Task<IntegrationBatch> ClaimAsync(string channel, FlowExecutionContext context, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var channelKey = WorkspaceDocument.Identity(channel);
            var rows = await session.Db.Messages.Where(row => row.ChannelKey == channelKey && row.State == "Pending")
                .OrderBy(row => row.CreatedUtcTicks).Take(100).ToListAsync(cancellationToken).ConfigureAwait(false);
            var payloads = new List<IntegrationPayload>();
            foreach (var row in rows)
            {
                if (row.FlowId is not null && !row.FlowId.Equals(context.FlowId, StringComparison.OrdinalIgnoreCase))
                    throw new FlowDurabilityException("An inbound route belongs to another flow. No queued messages were claimed.");
                var snapshots = JsonSerializer.Deserialize<List<FlowOrchestrationPayloadSnapshot>>(session.Decode(row.Payload, MessageIdentity(row.Id)), Json)
                    ?? throw new FlowDurabilityException("Invalid persisted message payload.");
                payloads.AddRange(snapshots.Select(snapshot =>
                {
                    var payload = snapshot.Restore();
                    if (!channel.StartsWith("http:", StringComparison.OrdinalIgnoreCase)) return payload;
                    var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
                    {
                        ["flowId"] = context.FlowId, ["executionId"] = context.ExecutionId
                    };
                    return payload with { Metadata = metadata };
                }));
                row.State = "Claimed";
                row.FlowId = context.FlowId;
                row.ExecutionId = context.ExecutionId;
                row.ExecutionKey = Key(context.ExecutionId);
                row.UpdatedUtcTicks = Now;
            }
            return new IntegrationBatch(payloads);
        }, true, cancellationToken);

    public Task CompleteAsync(FlowExecutionContext context, string channel, bool succeeded, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var key = Key(context.ExecutionId);
            var channelKey = WorkspaceDocument.Identity(channel);
            foreach (var row in await session.Db.Messages.Where(row => row.ExecutionKey == key && row.ChannelKey == channelKey
                && (row.State == "Claimed" || row.State == "Held")).ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                row.State = succeeded ? "Completed" : "Failed";
                row.UpdatedUtcTicks = Now;
            }
            return true;
        }, true, cancellationToken);

    public Task BeginAsync(PipelineDefinition pipeline, FlowExecutionContext context, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var key = Key(context.ExecutionId);
            var uncertain = await session.Db.Deliveries.Where(row => row.ExecutionKey == key && (row.State == "Unknown" || row.State == "InFlight")).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var retries = store.GetState().PendingRetries;
            if (uncertain.Any(row => !EligibleForConfirmation(session, row, retries, pipeline)))
                throw new FlowDurabilityException("This execution has an uncertain delivery. Reconcile destination evidence before resuming; no effect was replayed.");
            var execution = await session.Db.Executions.FindAsync([key], cancellationToken).ConfigureAwait(false);
            if (execution is null)
            {
                execution = new()
                {
                    Key = key, ExecutionId = context.ExecutionId, FlowId = context.FlowId, FlowKey = WorkspaceDocument.Identity(context.FlowId),
                    StartedUtcTicks = context.StartedAt.UtcTicks,
                    Definition = session.Encode(Serialize(pipeline), "execution:" + key)
                };
                session.Db.Executions.Add(execution);
            }
            execution.State = "Running";
            execution.UpdatedUtcTicks = Now;
            AddEvent(session, key, "execution", "Running", null);
            return true;
        }, true, cancellationToken);

    public Task FinishAsync(FlowExecutionContext context, string state, string? detail, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var key = Key(context.ExecutionId);
            var execution = await session.Db.Executions.SingleAsync(row => row.Key == key, cancellationToken).ConfigureAwait(false);
            execution.State = state;
            execution.UpdatedUtcTicks = Now;
            AddEvent(session, key, "execution", state, detail);
            return true;
        }, true, cancellationToken);

    public Task StageAsync(FlowExecutionContext context, string stage, string state, IntegrationBatch? batch, string? detail, CancellationToken cancellationToken, bool replaySafe = false)
        => UseAsync(async session =>
        {
            var key = Key(context.ExecutionId);
            AddEvent(session, key, stage, state, detail);
            var execution = await session.Db.Executions.SingleAsync(row => row.Key == key, cancellationToken).ConfigureAwait(false);
            var definition = System.Text.Json.Nodes.JsonNode.Parse(session.Decode(execution.Definition, "execution:" + key))
                ?? throw new FlowDurabilityException("Invalid execution definition.");
            definition["runtimeStage"] = JsonSerializer.SerializeToNode(new StageCursor(stage, state, replaySafe), Json);
            execution.Definition = session.Encode(definition.ToJsonString(), "execution:" + key);
            if (options.Value.CaptureStagePayloads && batch is { Count: > 0 })
            {
                var id = Guid.NewGuid();
                session.Db.Messages.Add(new()
                {
                    Id = id, Channel = stage + ":" + state, ChannelKey = WorkspaceDocument.Identity(stage),
                    FlowId = context.FlowId, ExecutionId = context.ExecutionId, ExecutionKey = key, State = "Snapshot",
                    CreatedUtcTicks = Now, UpdatedUtcTicks = Now,
                    Payload = session.Encode(Serialize(Snapshot(batch)), MessageIdentity(id))
                });
            }
            return true;
        }, true, cancellationToken);

    private sealed record StageCursor(string Stage, string State, bool ReplaySafe);

    private static StageCursor? ReadCursor(OperationalSession session, ExecutionRecord execution)
    {
        using var definition = JsonDocument.Parse(session.Decode(execution.Definition, "execution:" + execution.Key));
        return definition.RootElement.TryGetProperty("runtimeStage", out var cursor) ? cursor.Deserialize<StageCursor>(Json) : null;
    }

    public Task ValidateStageResumeAsync(FlowRetryState retry, bool replaySafeAvailable, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var execution = await session.Db.Executions.FindAsync([Key(retry.ExecutionId)], cancellationToken).ConfigureAwait(false);
            var cursor = execution is null ? null : ReadCursor(session, execution);
            var delivery = retry.StageId.StartsWith("delivery:", StringComparison.Ordinal) && retry.StageId.Contains(":deliver:", StringComparison.Ordinal);
            if (retry.ReplaySafeStage && !replaySafeAvailable)
                throw new FlowDurabilityException("The pinned stage no longer provides the replay-safe contract; recovery is held.");
            if (retry.ReplaySafeStage && (execution is null || retry.PipelineSnapshot is null
                || Serialize(JsonSerializer.Deserialize<PipelineDefinition>(session.Decode(execution.Definition, "execution:" + execution.Key), Json)) != Serialize(retry.PipelineSnapshot)))
                throw new FlowDurabilityException("The pure-stage continuation differs from the execution's pinned definition; recovery is held.");
            if (cursor is not null && (cursor.Stage != retry.StageId
                || (!delivery && cursor.State == "Started" && !(cursor.ReplaySafe && retry.ReplaySafeStage && replaySafeAvailable))))
                throw new FlowDurabilityException("An unsafe or different stage was started after this continuation. No stage was replayed.");
            return true;
        }, false, cancellationToken);

    private void AddEvent(OperationalSession session, string key, string stage, string state, string? detail)
    {
        var id = Guid.NewGuid();
        session.Db.ExecutionEvents.Add(new()
        {
            Id = id, ExecutionKey = key, Stage = stage, State = state, CreatedUtcTicks = Now,
            Detail = session.Encode(Serialize(detail), "event:" + id.ToString("N"))
        });
    }

    public sealed record DeliveryIntent(Guid Id, string State);

    public async Task<bool> PrepareDeliveryAsync(FlowExecutionContext context, string stage, ModuleStepDefinition step, IntegrationBatch batch, CancellationToken cancellationToken)
        => (await PrepareDeliveryIntentAsync(context, stage, new DeliveryActionDefinition { Deliver = step }, batch, cancellationToken).ConfigureAwait(false)).State != "Succeeded";

    public Task<DeliveryIntent> PrepareDeliveryIntentAsync(FlowExecutionContext context, string stage, DeliveryActionDefinition action, IntegrationBatch batch, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var key = WorkspaceDocument.Identity(context.ExecutionId, stage);
            var row = await session.Db.Deliveries.FindAsync([key], cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                row = new()
                {
                    Key = key, Id = Guid.NewGuid(), ExecutionKey = Key(context.ExecutionId), Stage = stage, UpdatedUtcTicks = Now,
                    Payload = session.Encode(Serialize(new { step = action.Deliver, payloads = Snapshot(batch), requireConfirmation = action.RequireConfirmation }), "delivery:" + key)
                };
                session.Db.Deliveries.Add(row);
            }
            else
            {
                using var saved = JsonDocument.Parse(session.Decode(row.Payload, "delivery:" + key));
                var root = saved.RootElement;
                var step = root.GetProperty("step").Deserialize<ModuleStepDefinition>(Json);
                var payloads = root.GetProperty("payloads").Deserialize<List<FlowOrchestrationPayloadSnapshot>>(Json);
                var confirmed = root.TryGetProperty("requireConfirmation", out var flag) && flag.ValueKind == JsonValueKind.True;
                if (Serialize(step) != Serialize(action.Deliver) || Serialize(payloads) != Serialize(Snapshot(batch)) || confirmed != action.RequireConfirmation)
                    throw new FlowDurabilityException("The delivery intent does not match the pinned continuation; no effect was replayed.");
                if (row.State == "Succeeded" && root.TryGetProperty("responses", out var responses))
                {
                    var snapshots = responses.Deserialize<List<FlowOrchestrationPayloadSnapshot>>(Json)
                        ?? throw new FlowDurabilityException("Invalid completed delivery response snapshots.");
                    context.CaptureResponse(new IntegrationBatch(snapshots.Select(snapshot => snapshot.Restore()).ToArray()));
                }
                if (row.State != "Prepared" && row.State != "Succeeded" && !(confirmed && (row.State == "Unknown" || row.State == "InFlight")))
                    throw new FlowDurabilityException("Delivery outcome is uncertain; automatic replay is blocked.");
            }
            return new DeliveryIntent(row.Id, row.State);
        }, true, cancellationToken);

    public Task DeliveryStateAsync(FlowExecutionContext context, string stage, string state, CancellationToken cancellationToken, int responseStart = 0)
        => UseAsync(async session =>
        {
            var key = WorkspaceDocument.Identity(context.ExecutionId, stage);
            var row = await session.Db.Deliveries.SingleAsync(row => row.Key == key, cancellationToken).ConfigureAwait(false);
            row.State = state;
            row.UpdatedUtcTicks = Now;
            if (state == "Succeeded")
            {
                var payload = System.Text.Json.Nodes.JsonNode.Parse(session.Decode(row.Payload, "delivery:" + key))
                    ?? throw new FlowDurabilityException("Invalid delivery intent payload.");
                payload["responses"] = JsonSerializer.SerializeToNode(Snapshot(new IntegrationBatch(context.CapturedResponses.Skip(responseStart).ToArray())), Json);
                row.Payload = session.Encode(payload.ToJsonString(), "delivery:" + key);
            }
            return true;
        }, true, cancellationToken);

    public Task<bool> IsBlockedAsync(string flowId, CancellationToken cancellationToken)
        => UseAsync(session => session.Db.Executions.AnyAsync(row => row.FlowKey == WorkspaceDocument.Identity(flowId)
            && (row.State == "Held" || row.State == "Running"), cancellationToken), false, cancellationToken);

    public Task<IReadOnlyList<string>> PendingChannelsAsync(CancellationToken cancellationToken)
        => UseAsync<IReadOnlyList<string>>(async session => await session.Db.Messages.Where(row => row.State == "Pending")
            .Select(row => row.ChannelKey).Distinct().ToArrayAsync(cancellationToken).ConfigureAwait(false), false, cancellationToken);

    public Task<bool> IsDeliveryHeldAsync(string executionId, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var rows = await session.Db.Deliveries.Where(row => row.ExecutionKey == Key(executionId)
                && (row.State == "Unknown" || row.State == "InFlight")).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var retries = store.GetState().PendingRetries;
            if (rows.Any(row => !EligibleForConfirmation(session, row, retries, null))) return true;
            var execution = await session.Db.Executions.FindAsync([Key(executionId)], cancellationToken).ConfigureAwait(false);
            var cursor = execution is null ? null : ReadCursor(session, execution);
            return cursor is { State: "Started", ReplaySafe: false } && !cursor.Stage.Contains(":deliver:", StringComparison.Ordinal);

        }, false, cancellationToken);

    private static bool EligibleForConfirmation(OperationalSession session, DeliveryRecord row, IReadOnlyList<FlowRetryState> retries, PipelineDefinition? pipeline)
    {
        var retry = retries.SingleOrDefault(item => Key(item.ExecutionId) == row.ExecutionKey && item.StageId == row.Stage);
        if (retry?.PipelineSnapshot is not { } pinned || (pipeline is not null && Serialize(pinned) != Serialize(pipeline))) return false;
        var parts = row.Stage.Split(':');
        if (parts.Length != 4 || parts[0] != "delivery" || parts[2] != "deliver"
            || !int.TryParse(parts[1], out var route) || !int.TryParse(parts[3], out var index)
            || route < 0 || route >= pinned.Deliveries.Count || index < 0 || index >= pinned.Deliveries[route].Delivers.Count) return false;
        var action = pinned.Deliveries[route].Delivers[index];
        if (!action.RequireConfirmation) return false;
        using var payload = JsonDocument.Parse(session.Decode(row.Payload, "delivery:" + row.Key));
        return payload.RootElement.TryGetProperty("requireConfirmation", out var flag) && flag.ValueKind == JsonValueKind.True
            && payload.RootElement.TryGetProperty("step", out var step)
            && Serialize(step.Deserialize<ModuleStepDefinition>(Json)) == Serialize(action.Deliver);
    }

    public Task RecoverAsync(CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            foreach (var row in await session.Db.Executions.Where(row => row.State == "Running").ToListAsync(cancellationToken).ConfigureAwait(false))
            {
                var uncertain = await session.Db.Deliveries.AnyAsync(delivery => delivery.ExecutionKey == row.Key
                    && (delivery.State == "Unknown" || delivery.State == "InFlight"), cancellationToken).ConfigureAwait(false);
                row.State = !uncertain && await session.Db.Retries.AnyAsync(retry => retry.ExecutionKey == row.Key, cancellationToken).ConfigureAwait(false) ? "Retrying" : "Held";
                row.UpdatedUtcTicks = Now;
                AddEvent(session, row.Key, "recovery", row.State, "Interrupted execution retained. Uncertain effects are not replayed automatically.");
            }
            foreach (var row in await session.Db.Deliveries.Where(row => row.State == "InFlight").ToListAsync(cancellationToken).ConfigureAwait(false)) row.State = "Unknown";
            foreach (var row in await session.Db.Messages.Where(row => row.State == "Claimed").ToListAsync(cancellationToken).ConfigureAwait(false)) row.State = "Held";
            return true;
        }, true, cancellationToken);

    public Task<IReadOnlyList<ExecutionRecord>> ListAsync(string? flowId, int take, CancellationToken cancellationToken)
        => UseAsync<IReadOnlyList<ExecutionRecord>>(async session =>
        {
            var query = session.Db.Executions.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(flowId)) query = query.Where(row => row.FlowKey == WorkspaceDocument.Identity(flowId));
            return await query.OrderByDescending(row => row.StartedUtcTicks).Take(take).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        }, false, cancellationToken);

    public Task<object> DetailAsync(string executionId, CancellationToken cancellationToken)
        => UseAsync<object>(async session =>
        {
            var key = Key(executionId);
            var execution = await session.Db.Executions.SingleOrDefaultAsync(row => row.Key == key, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Execution not found.");
            var events = await session.Db.ExecutionEvents.Where(row => row.ExecutionKey == key).OrderBy(row => row.CreatedUtcTicks).Take(1000).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var messages = await session.Db.Messages.Where(row => row.ExecutionKey == key).OrderBy(row => row.CreatedUtcTicks).Take(1000).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            var deliveries = await session.Db.Deliveries.Where(row => row.ExecutionKey == key).Take(1000).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return new
            {
                execution = Summary(execution),
                events = events.Select(row => new { row.Id, row.Stage, row.State, row.CreatedUtcTicks, detail = JsonSerializer.Deserialize<string?>(session.Decode(row.Detail, "event:" + row.Id.ToString("N")), Json) }),
                messages = messages.Select(row => new { row.Id, row.Channel, row.State, row.ExecutionId, row.CreatedUtcTicks }),
                deliveries = deliveries.Select(row => new { row.Id, row.Stage, row.State, row.UpdatedUtcTicks })
            };
        }, false, cancellationToken);

    public static object Summary(ExecutionRecord row) => new { id = row.ExecutionId, row.FlowId, row.State, row.StartedUtcTicks, row.UpdatedUtcTicks };

    public Task<string> ReadMessageAsync(Guid id, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var row = await session.Db.Messages.SingleOrDefaultAsync(row => row.Id == id, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Message not found or expired.");
            return session.Decode(row.Payload, MessageIdentity(id));
        }, false, cancellationToken);

    public Task<object> MessagesAsync(int take, CancellationToken cancellationToken)
        => UseAsync<object>(async session =>
        {
            var rows = await session.Db.Messages.OrderByDescending(row => row.State == "Pending" || row.State == "Claimed" || row.State == "Held")
                .ThenByDescending(row => row.CreatedUtcTicks).Take(take).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(row => new { row.Id, row.Channel, row.State, row.ExecutionId, row.CreatedUtcTicks }).ToArray();
        }, false, cancellationToken);

    public Task ConfirmDeliveredAsync(string executionId, Guid deliveryId, string evidence, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            if (string.IsNullOrWhiteSpace(evidence) || evidence.Trim().Length is < 10 or > 4096)
                throw new ArgumentException("Supply 10-4096 characters of independently verified destination evidence.");
            var key = Key(executionId);
            var delivery = await session.Db.Deliveries.SingleOrDefaultAsync(row => row.Id == deliveryId && row.ExecutionKey == key, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Delivery not found.");
            if (delivery.State != "Unknown") throw new FlowDurabilityException("Refresh history: only an Unknown delivery can be reconciled, never an active or already confirmed effect.");
            using var payload = JsonDocument.Parse(session.Decode(delivery.Payload, "delivery:" + delivery.Key));
            var step = payload.RootElement.GetProperty("step").Deserialize<ModuleStepDefinition>(Json)
                ?? throw new FlowDurabilityException("Invalid persisted delivery intent.");
            if (step.Settings.TryGetValue("expectsResponse", out var expectsResponse) && bool.TryParse(expectsResponse, out var expects) && expects)
                throw new FlowDurabilityException("Request-response deliveries require verified response data; this one-way confirmation cannot safely resume them.");
            var execution = await session.Db.Executions.SingleAsync(row => row.Key == key, cancellationToken).ConfigureAwait(false);
            var retryKey = WorkspaceDocument.Identity(execution.FlowId, executionId, delivery.Stage);
            if (!await session.Db.Retries.AnyAsync(row => row.Key == retryKey, cancellationToken).ConfigureAwait(false))
                throw new FlowDurabilityException("No saved continuation exists for this delivery. This execution cannot safely resume through delivery confirmation.");
            delivery.State = "Succeeded";
            delivery.UpdatedUtcTicks = Now;
            execution.State = "Retrying";
            execution.UpdatedUtcTicks = Now;
            AddEvent(session, key, delivery.Stage, "Reconciled", evidence.Trim());
            return true;
        }, true, cancellationToken);

    public Task CloseHeldAsync(string executionId, string evidence, CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            if (string.IsNullOrWhiteSpace(evidence) || evidence.Trim().Length is < 10 or > 4096)
                throw new ArgumentException("Supply 10-4096 characters documenting independent review and the decision to abandon held input without replay.");
            var key = Key(executionId);
            var execution = await session.Db.Executions.SingleOrDefaultAsync(row => row.Key == key, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("Execution not found.");
            if (execution.State != "Held") throw new FlowDurabilityException("Refresh history: only a held execution can be closed without replay.");
            if (await session.Db.Retries.AnyAsync(row => row.ExecutionKey == key, cancellationToken).ConfigureAwait(false)
                || await session.Db.Checkpoints.AnyAsync(row => row.FlowKey == execution.FlowKey, cancellationToken).ConfigureAwait(false))
                throw new FlowDurabilityException("Resolve the existing retry/checkpoint recovery first. This action cannot discard a saved runtime continuation.");
            foreach (var message in await session.Db.Messages.Where(row => row.ExecutionKey == key && (row.State == "Claimed" || row.State == "Held"))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false))
            {
                message.State = "Abandoned";
                message.UpdatedUtcTicks = Now;
            }
            execution.State = "Closed";
            execution.UpdatedUtcTicks = Now;
            AddEvent(session, key, "recovery", "Closed", evidence.Trim());
            return true;
        }, true, cancellationToken);

    public Task<int> PurgeAsync(CancellationToken cancellationToken)
        => UseAsync(async session =>
        {
            var cutoff = timeProvider.GetUtcNow().AddDays(-options.Value.RetentionDays).UtcTicks;
            var old = await session.Db.Executions.Where(row => (row.State == "Completed" || row.State == "Failed" || row.State == "Closed") && row.UpdatedUtcTicks < cutoff
                && !session.Db.Retries.Any(retry => retry.ExecutionKey == row.Key)
                && !session.Db.Checkpoints.Any(checkpoint => checkpoint.FlowKey == row.FlowKey)
                && !session.Db.Messages.Any(message => message.ExecutionKey == row.Key && (message.State == "Pending" || message.State == "Claimed" || message.State == "Held"))
                && !session.Db.Deliveries.Any(delivery => delivery.ExecutionKey == row.Key && delivery.State != "Succeeded"))
                .OrderBy(row => row.UpdatedUtcTicks).Take(100).ToArrayAsync(cancellationToken).ConfigureAwait(false);
            foreach (var execution in old)
            {
                var messages = await session.Db.Messages.Where(row => row.ExecutionKey == execution.Key && !row.RetainForConfirmation).ToArrayAsync(cancellationToken).ConfigureAwait(false);
                session.Db.Messages.RemoveRange(messages);
                session.Db.Executions.Remove(execution);
            }
            return session.Db.ChangeTracker.Entries<ExecutionRecord>().Count(entry => entry.State == EntityState.Deleted);
        }, true, cancellationToken);
}
