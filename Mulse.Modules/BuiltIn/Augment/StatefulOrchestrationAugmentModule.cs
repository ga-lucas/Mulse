using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Mulse.Modules.BuiltIn.Augment;

public sealed class StatefulOrchestrationAugmentModule(
    IFlowOrchestrationStateStore orchestrationStateStore,
    TimeProvider timeProvider,
    ILogger<StatefulOrchestrationAugmentModule> logger) : IOrchestrationAugmentModule
{
    private const string ModuleId = "stateful-orchestration-augment";
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    private static readonly IReadOnlyList<ModuleSettingDescriptor> SettingDescriptors =
    [
        new("mode", "Mode", "Whether this step should resume-or-wait, suspend immediately, or mark the orchestration instance complete after a successful run.", true, ModuleSettingInputKind.Select, "ResumeOrWait",
        [
            new ModuleSettingOption("ResumeOrWait", "Resume or wait"),
            new ModuleSettingOption("Suspend", "Suspend"),
            new ModuleSettingOption("Complete", "Complete on success")
        ]),
        new("bookmark", "Bookmark", "Logical wait point name used to ensure resumes happen at the expected orchestration step.", false),
        new("correlationSource", "Correlation source", "Where to read the correlation value from.", true, ModuleSettingInputKind.Select, "Metadata",
        [
            new ModuleSettingOption("Metadata", "Metadata"),
            new ModuleSettingOption("Payload", "Payload"),
            new ModuleSettingOption("PayloadName", "Payload name"),
            new ModuleSettingOption("ContentType", "Content type"),
            new ModuleSettingOption("Literal", "Literal"),
            new ModuleSettingOption("Value", "Literal")
        ]),
        new("correlationPath", "Correlation path", "Metadata key or JSONPath used when the correlation source reads from metadata or payload.", false),
        new("correlationValue", "Literal correlation value", "Literal correlation value used when the source is Literal or Value.", false),
        new("stateCaptureJson", "State capture JSON", "Optional JSON array describing orchestration state values to persist with the checkpoint.", false, ModuleSettingInputKind.TextArea, "[]"),
        new("metadataPrefix", "Metadata prefix", "Prefix used when restoring orchestration state into payload metadata.", false, ModuleSettingInputKind.Text, "orchestration"),
        new("suspensionReason", "Suspension reason", "Optional reason message attached when the flow suspends.", false)
    ];

    private static readonly ModuleRecommendationProfile Recommendation = new(
        [ModuleDataFormat.Json],
        capabilities: [ModuleCapability.Transformation, ModuleCapability.Decision]);

    public ModuleDescriptor Descriptor { get; } = new(
        ModuleId,
        "Stateful orchestration augment",
        ModuleKind.OrchestrationAugment,
        "Persists correlated orchestration checkpoints so flows can wait for follow-up messages, resume with saved payloads and state, and complete after successful delivery.",
        SettingDescriptors,
        Recommendation);

    public async Task<IntegrationBatch> AugmentAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var mode = ParseMode(step);
        var metadataPrefix = ModuleSettingReader.GetOptional(step.Settings, "metadataPrefix") ?? "orchestration";
        var correlationSource = ParseSource(step);
        var correlationKey = ResolveCorrelationKey(batch, correlationSource, step);
        var bookmark = RequiresBookmark(mode)
            ? ModuleSettingReader.GetRequired(step.Settings, "bookmark", Descriptor.Id)
            : ModuleSettingReader.GetOptional(step.Settings, "bookmark") ?? string.Empty;
        var capturedState = CaptureState(batch, step, correlationKey);

        return mode switch
        {
            StatefulOrchestrationMode.ResumeOrWait => await ResumeOrWaitAsync(context, batch, correlationKey, bookmark, metadataPrefix, capturedState, step, cancellationToken).ConfigureAwait(false),
            StatefulOrchestrationMode.Suspend => await SuspendAsync(context, batch, correlationKey, bookmark, capturedState, step, cancellationToken).ConfigureAwait(false),
            StatefulOrchestrationMode.Complete => await CompleteAsync(context, batch, correlationKey, metadataPrefix, cancellationToken).ConfigureAwait(false),
            _ => batch
        };
    }

    private async Task<IntegrationBatch> ResumeOrWaitAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        string correlationKey,
        string bookmark,
        string metadataPrefix,
        IReadOnlyDictionary<string, string> capturedState,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var checkpoint = await orchestrationStateStore.GetAsync(context.FlowId, correlationKey, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            var created = CreateCheckpoint(context.FlowId, correlationKey, bookmark, batch, capturedState, instanceId: Guid.CreateVersion7().ToString());
            await orchestrationStateStore.UpsertAsync(created, cancellationToken).ConfigureAwait(false);
            context.SetOrchestrationInstance(created.InstanceId, created.CorrelationKey, created.Bookmark, isResumed: false);
            context.ReplaceOrchestrationState(created.StateValues);
            context.Suspend(ModuleSettingReader.GetOptional(step.Settings, "suspensionReason") ?? $"Waiting for correlated message at bookmark '{bookmark}'.");
            logger.LogInformation(
                "Flow {FlowId} execution {ExecutionId} suspended new orchestration instance {InstanceId} on correlation key {CorrelationKey} at bookmark {Bookmark}.",
                context.FlowId,
                context.ExecutionId,
                created.InstanceId,
                correlationKey,
                bookmark);
            return ApplyOrchestrationMetadata(batch, context, metadataPrefix);
        }

        if (!string.Equals(checkpoint.Bookmark, bookmark, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Module '{Descriptor.Id}' expected bookmark '{bookmark}' for flow '{context.FlowId}' correlation '{correlationKey}', but the persisted checkpoint is waiting at '{checkpoint.Bookmark}'.");
        }

        context.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, checkpoint.Bookmark, isResumed: true);
        context.ReplaceOrchestrationState(checkpoint.StateValues);
        var mergedBatch = MergeBatches(checkpoint, batch);
        logger.LogInformation(
            "Flow {FlowId} execution {ExecutionId} resumed orchestration instance {InstanceId} on correlation key {CorrelationKey} at bookmark {Bookmark} with {PayloadCount} payload(s).",
            context.FlowId,
            context.ExecutionId,
            checkpoint.InstanceId,
            correlationKey,
            bookmark,
            mergedBatch.Count);
        return ApplyOrchestrationMetadata(mergedBatch, context, metadataPrefix);
    }

    private async Task<IntegrationBatch> SuspendAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        string correlationKey,
        string bookmark,
        IReadOnlyDictionary<string, string> capturedState,
        ModuleStepDefinition step,
        CancellationToken cancellationToken)
    {
        var existing = await orchestrationStateStore.GetAsync(context.FlowId, correlationKey, cancellationToken).ConfigureAwait(false);
        var instanceId = existing?.InstanceId ?? context.OrchestrationInstanceId ?? Guid.CreateVersion7().ToString();
        var checkpoint = CreateCheckpoint(context.FlowId, correlationKey, bookmark, batch, capturedState, instanceId);
        await orchestrationStateStore.UpsertAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        context.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, checkpoint.Bookmark, isResumed: existing is not null || context.IsResumedOrchestration);
        context.ReplaceOrchestrationState(checkpoint.StateValues);
        context.Suspend(ModuleSettingReader.GetOptional(step.Settings, "suspensionReason") ?? $"Waiting for correlated message at bookmark '{bookmark}'.");
        logger.LogInformation(
            "Flow {FlowId} execution {ExecutionId} persisted orchestration instance {InstanceId} on correlation key {CorrelationKey} at bookmark {Bookmark} and suspended.",
            context.FlowId,
            context.ExecutionId,
            checkpoint.InstanceId,
            correlationKey,
            bookmark);
        return batch;
    }

    private async Task<IntegrationBatch> CompleteAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        string correlationKey,
        string metadataPrefix,
        CancellationToken cancellationToken)
    {
        var checkpoint = await orchestrationStateStore.GetAsync(context.FlowId, correlationKey, cancellationToken).ConfigureAwait(false);
        if (checkpoint is null)
        {
            logger.LogInformation(
                "Flow {FlowId} execution {ExecutionId} marked orchestration correlation {CorrelationKey} complete, but no persisted checkpoint was present.",
                context.FlowId,
                context.ExecutionId,
                correlationKey);
            return ApplyOrchestrationMetadata(batch, context, metadataPrefix);
        }

        context.SetOrchestrationInstance(checkpoint.InstanceId, checkpoint.CorrelationKey, checkpoint.Bookmark, isResumed: context.IsResumedOrchestration);
        context.ReplaceOrchestrationState(checkpoint.StateValues);
        context.MarkCheckpointForCompletion(checkpoint.FlowId, checkpoint.CorrelationKey);
        logger.LogInformation(
            "Flow {FlowId} execution {ExecutionId} marked orchestration instance {InstanceId} complete on correlation key {CorrelationKey}; checkpoint will be deleted after successful completion.",
            context.FlowId,
            context.ExecutionId,
            checkpoint.InstanceId,
            correlationKey);
        return ApplyOrchestrationMetadata(batch, context, metadataPrefix);
    }

    private FlowOrchestrationCheckpoint CreateCheckpoint(
        string flowId,
        string correlationKey,
        string bookmark,
        IntegrationBatch batch,
        IReadOnlyDictionary<string, string> stateValues,
        string instanceId)
    {
        return new FlowOrchestrationCheckpoint
        {
            FlowId = flowId,
            InstanceId = instanceId,
            CorrelationKey = correlationKey,
            Bookmark = bookmark,
            UpdatedAt = timeProvider.GetUtcNow(),
            StateValues = new Dictionary<string, string>(stateValues, StringComparer.OrdinalIgnoreCase),
            Payloads = batch.Payloads.Select(CreateSnapshot).ToList()
        };
    }

    private static FlowOrchestrationPayloadSnapshot CreateSnapshot(IntegrationPayload payload)
    {
        return new FlowOrchestrationPayloadSnapshot
        {
            Name = payload.Name,
            ContentBase64 = Convert.ToBase64String(payload.Content.ToArray()),
            ContentType = payload.ContentType,
            Metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static IntegrationBatch MergeBatches(FlowOrchestrationCheckpoint checkpoint, IntegrationBatch currentBatch)
    {
        var restoredPayloads = checkpoint.Payloads.Select(snapshot => new IntegrationPayload(
            snapshot.Name,
            BinaryData.FromBytes(Convert.FromBase64String(snapshot.ContentBase64)),
            snapshot.ContentType,
            new Dictionary<string, string>(snapshot.Metadata, StringComparer.OrdinalIgnoreCase)));
        return new IntegrationBatch(restoredPayloads.Concat(currentBatch.Payloads).ToArray());
    }

    private static IntegrationBatch ApplyOrchestrationMetadata(IntegrationBatch batch, FlowExecutionContext context, string metadataPrefix)
    {
        var normalizedPrefix = string.IsNullOrWhiteSpace(metadataPrefix) ? "orchestration" : metadataPrefix.Trim();
        var transformed = batch.Payloads.Select(payload =>
        {
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(context.OrchestrationInstanceId))
            {
                metadata[$"{normalizedPrefix}.instanceId"] = context.OrchestrationInstanceId;
            }

            if (!string.IsNullOrWhiteSpace(context.OrchestrationCorrelationKey))
            {
                metadata[$"{normalizedPrefix}.correlationKey"] = context.OrchestrationCorrelationKey;
            }

            if (!string.IsNullOrWhiteSpace(context.OrchestrationBookmark))
            {
                metadata[$"{normalizedPrefix}.bookmark"] = context.OrchestrationBookmark;
            }

            metadata[$"{normalizedPrefix}.resumed"] = context.IsResumedOrchestration.ToString();

            foreach (var entry in context.OrchestrationState)
            {
                metadata[$"{normalizedPrefix}.state.{entry.Key}"] = entry.Value;
            }

            return new IntegrationPayload(payload.Name, payload.Content, payload.ContentType, metadata);
        }).ToArray();

        return new IntegrationBatch(transformed);
    }

    private IReadOnlyDictionary<string, string> CaptureState(IntegrationBatch batch, ModuleStepDefinition step, string correlationKey)
    {
        var captureDefinitions = ParseStateCaptureDefinitions(ModuleSettingReader.GetOptional(step.Settings, "stateCaptureJson"));
        if (captureDefinitions.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["correlationKey"] = correlationKey
            };
        }

        if (batch.Count == 0)
        {
            throw new InvalidOperationException($"Module '{Descriptor.Id}' cannot capture orchestration state from an empty batch.");
        }

        var payload = batch.Payloads[0];
        var state = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["correlationKey"] = correlationKey
        };

        foreach (var definition in captureDefinitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Key))
            {
                continue;
            }

            var resolved = DecisionRuntime.ResolveActionValue(payload, new DecisionActionDefinition
            {
                ValueSource = definition.Source,
                SourcePath = definition.Path,
                Value = definition.Value
            }, Descriptor.Id);

            if (!string.IsNullOrWhiteSpace(resolved))
            {
                state[definition.Key] = resolved;
            }
        }

        return state;
    }

    private static IReadOnlyList<OrchestrationStateCaptureDefinition> ParseStateCaptureDefinitions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<OrchestrationStateCaptureDefinition[]>(json, SerializerOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new ArgumentException($"Module '{ModuleId}' has invalid stateCaptureJson content.", nameof(json), exception);
        }
    }

    private static StatefulOrchestrationMode ParseMode(ModuleStepDefinition step)
    {
        var raw = ModuleSettingReader.GetRequired(step.Settings, "mode", ModuleId);
        if (Enum.TryParse<StatefulOrchestrationMode>(raw, ignoreCase: true, out var mode))
        {
            return mode;
        }

        throw new ArgumentException($"Module '{ModuleId}' has invalid mode '{raw}'.", nameof(step));
    }

    private static DecisionValueSourceKind ParseSource(ModuleStepDefinition step)
    {
        var raw = ModuleSettingReader.GetRequired(step.Settings, "correlationSource", ModuleId);
        if (Enum.TryParse<DecisionValueSourceKind>(raw, ignoreCase: true, out var source))
        {
            return source == DecisionValueSourceKind.Literal || string.Equals(raw, "Value", StringComparison.OrdinalIgnoreCase)
                ? DecisionValueSourceKind.Literal
                : source;
        }

        throw new ArgumentException($"Module '{ModuleId}' has invalid correlationSource '{raw}'.", nameof(step));
    }

    private static string ResolveCorrelationKey(IntegrationBatch batch, DecisionValueSourceKind source, ModuleStepDefinition step)
    {
        if (batch.Count == 0)
        {
            throw new InvalidOperationException($"Module '{ModuleId}' requires at least one payload to resolve a correlation key.");
        }

        var keys = batch.Payloads.Select(payload => ResolveCorrelationKey(payload, source, step))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (keys.Length != 1)
        {
            throw new InvalidOperationException($"Module '{ModuleId}' requires every payload in the batch to resolve to the same correlation key.");
        }

        return keys[0];
    }

    private static string ResolveCorrelationKey(IntegrationPayload payload, DecisionValueSourceKind source, ModuleStepDefinition step)
    {
        var action = new DecisionActionDefinition
        {
            ValueSource = source,
            SourcePath = ModuleSettingReader.GetOptional(step.Settings, "correlationPath"),
            Value = ModuleSettingReader.GetOptional(step.Settings, "correlationValue")
        };
        var value = DecisionRuntime.ResolveActionValue(payload, action, ModuleId);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Module '{ModuleId}' could not resolve a correlation key for payload '{payload.Name}'.");
        }

        return value;
    }

    private static bool RequiresBookmark(StatefulOrchestrationMode mode)
        => mode is StatefulOrchestrationMode.ResumeOrWait or StatefulOrchestrationMode.Suspend;

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
