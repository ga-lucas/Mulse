using System.Collections.Concurrent;
using Mulse.Modules;

namespace Service.Execution;

/// <summary>
/// Executes configured flows as a sequence of named, independently retryable "stages":
/// <c>fetch:{fetchNodeId}</c> for every fetch node and <c>parse:{parseNodeId}</c> for every parse node in the flow's
/// source graph (run together in one topological order of <see cref="FetchNodeDefinition.InputParseIds"/> and
/// <see cref="ParseNodeDefinition.FetchNodeIds"/>), then <c>augment:{index}</c> over the merged parsed batch, then
/// per delivery route <c>delivery:{route}:render</c> / <c>delivery:{route}:deliver</c>. Each stage's effective
/// <see cref="RetryPolicyDefinition"/> (step override, falling back to the flow-level default, falling back to a
/// single attempt) governs what happens when that stage throws: if another attempt is allowed, the stage's input
/// batch (plus, for source-graph stages, the already resolved per-fetch-node raw batches and per-parse-node parsed
/// batches, and for delivery stages the shared post-augment batch) is snapshotted to disk via
/// <see cref="IFlowRetryStateStore"/> and execution returns immediately with a
/// <see cref="FlowExecutionOutcome.Retrying"/> result; a separate background driver is responsible for waiting
/// out the delay and calling <see cref="ResumeRetryAsync"/>. Because resumption reads the same durable state a
/// crash would have left behind, this also provides crash/restart recovery for free.
/// </summary>
public sealed partial class FlowRuntime(
    IFlowDefinitionService flowDefinitionService,
    IModuleCatalog moduleCatalog,
    IFlowOrchestrationStateStore orchestrationStateStore,
    IFlowRetryStateStore flowRetryStateStore,
    IConfigValueResolver configValueResolver,
    TimeProvider timeProvider,
    ILogger<FlowRuntime> logger) : IFlowRuntime
{
    private const string ConditionJsonSetting = "conditionJson";
    private const string ExpectsResponseSetting = "expectsResponse";
    private const string SourceIdMetadataKey = "sourceId";
    private const string FetchStagePrefix = "fetch:";
    private const string ParseStagePrefix = "parse:";
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _flowLocks = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<PipelineDefinition> GetConfiguredFlows()
    {
        return flowDefinitionService.GetAll();
    }

    public async Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken cancellationToken)
    {
        var pipeline = GetConfiguredFlow(flowId);
        var gate = _flowLocks.GetOrAdd(pipeline.Id, static _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var startedAt = timeProvider.GetUtcNow();
            var context = new FlowExecutionContext(pipeline.Id, Guid.CreateVersion7().ToString(), startedAt);

            try
            {
                return await RunSourceGraphOnwardAsync(
                    pipeline, context, startedAt,
                    resolvedFetches: new Dictionary<string, IntegrationBatch>(StringComparer.OrdinalIgnoreCase),
                    resolvedParses: new Dictionary<string, IntegrationBatch>(StringComparer.OrdinalIgnoreCase),
                    resumeStageId: null, resumeInput: null, resumeAttempts: 0,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (FlowRetryScheduledSignal signal)
            {
                return signal.Result;
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Resolves every fetch and parse node in the flow's source graph (in one combined topological order of
    /// <see cref="FetchNodeDefinition.InputParseIds"/> and <see cref="ParseNodeDefinition.FetchNodeIds"/>) and then
    /// hands the merged, <c>sourceId</c>-tagged output of every parse node to the augment chain.
    /// <paramref name="resolvedFetches"/> (raw batches) and <paramref name="resolvedParses"/> (parsed, tagged
    /// batches) carry nodes already resolved by an earlier attempt of this same execution, so a resumed run never
    /// re-fetches upstream fetch nodes. <paramref name="resumeStageId"/> names the stalled stage being resumed
    /// (<c>fetch:{id}</c> or <c>parse:{id}</c>), if any.
    /// </summary>
    private async Task<FlowExecutionResult> RunSourceGraphOnwardAsync(
        PipelineDefinition pipeline,
        FlowExecutionContext context,
        DateTimeOffset startedAt,
        Dictionary<string, IntegrationBatch> resolvedFetches,
        Dictionary<string, IntegrationBatch> resolvedParses,
        string? resumeStageId,
        IntegrationBatch? resumeInput,
        int resumeAttempts,
        CancellationToken cancellationToken)
    {
        var orderedSteps = OrderSourceGraphTopologically(pipeline);

        foreach (var step in orderedSteps)
        {
            if (step.Kind == SourceGraphNodeKind.Fetch)
            {
                var fetchNode = step.Fetch!;
                if (resolvedFetches.ContainsKey(fetchNode.Id))
                {
                    continue;
                }

                var stageId = $"{FetchStagePrefix}{fetchNode.Id}";
                var isResumed = string.Equals(stageId, resumeStageId, StringComparison.OrdinalIgnoreCase);
                var fetchInput = isResumed && resumeInput is not null
                    ? resumeInput
                    : MergeBatches(DistinctIds(fetchNode.InputParseIds).Select(inputParseId => ResolveInputParse(pipeline, resolvedParses, fetchNode, inputParseId)));
                var fetchStep = fetchNode.Fetch;

                resolvedFetches[fetchNode.Id] = await RunStageAsync(
                    pipeline, context, startedAt, stageId, EffectiveRetry(pipeline, fetchStep), isResumed ? resumeAttempts : 0,
                    fetchInput, committedAtomicDeliveries: [], postAugmentBatchForSnapshot: null,
                    resolvedFetchesForSnapshot: resolvedFetches, resolvedParsesForSnapshot: resolvedParses,
                    invoke: async ct =>
                    {
                        await using var fetch = await moduleCatalog.LeaseFetchAsync(fetchStep.Module, ct).ConfigureAwait(false);
                        return await fetch.Module.FetchAsync(context, fetchInput, fetchStep, ct).ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var parseNode = step.Parse!;
                if (resolvedParses.ContainsKey(parseNode.Id))
                {
                    continue;
                }

                var stageId = $"{ParseStagePrefix}{parseNode.Id}";
                var isResumed = string.Equals(stageId, resumeStageId, StringComparison.OrdinalIgnoreCase);
                var parseStep = parseNode.Parse;

                // Every input fetch node's raw batch is parsed independently (same module + settings), so a parse
                // module never sees documents from two fetches in one call. On resume the raw batches come from the
                // restored resolvedFetches rather than the snapshotted (merged) stage input.
                var rawBatches = DistinctIds(parseNode.FetchNodeIds)
                    .Select(fetchNodeId => ResolveInputFetch(pipeline, resolvedFetches, parseNode, fetchNodeId))
                    .ToArray();

                var parsedBatch = await RunStageAsync(
                    pipeline, context, startedAt, stageId, EffectiveRetry(pipeline, parseStep), isResumed ? resumeAttempts : 0,
                    MergeBatches(rawBatches), committedAtomicDeliveries: [], postAugmentBatchForSnapshot: null,
                    resolvedFetchesForSnapshot: resolvedFetches, resolvedParsesForSnapshot: resolvedParses,
                    invoke: async ct =>
                    {
                        var parsedBatches = new List<IntegrationBatch>(rawBatches.Length);
                        foreach (var rawBatch in rawBatches)
                        {
                            await using var parse = await moduleCatalog.LeaseParseAsync(parseStep.Module, ct).ConfigureAwait(false);
                            parsedBatches.Add(await ProcessConditionalStepAsync(
                                rawBatch, parseStep, applicableBatch => parse.Module.ParseAsync(context, applicableBatch, parseStep, ct), ct).ConfigureAwait(false));
                        }

                        return MergeBatches(parsedBatches);
                    },
                    cancellationToken).ConfigureAwait(false);

                resolvedParses[parseNode.Id] = TagWithParseId(parsedBatch, parseNode.Id);
            }
        }

        // Every parse node's output (each payload tagged with its parse node id under "sourceId") forms the augment
        // input, including parse nodes whose output was also consumed as another fetch node's input.
        var mergedBatch = MergeBatches(orderedSteps
            .Where(static step => step.Kind == SourceGraphNodeKind.Parse)
            .Select(step => resolvedParses[step.Id]));
        return await RunAugmentOnwardAsync(pipeline, context, startedAt, augmentIndex: 0, mergedBatch, attemptsAlreadyMade: 0, cancellationToken).ConfigureAwait(false);
    }

    private static IntegrationBatch ResolveInputParse(
        PipelineDefinition pipeline,
        IReadOnlyDictionary<string, IntegrationBatch> resolvedParses,
        FetchNodeDefinition fetchNode,
        string inputParseId)
    {
        if (resolvedParses.TryGetValue(inputParseId, out var batch))
        {
            return batch;
        }

        throw new InvalidOperationException(
            $"Flow '{pipeline.Id}' fetch node '{fetchNode.Id}' declares input parse node '{inputParseId}', which is not a parse node of this flow.");
    }

    private static IntegrationBatch ResolveInputFetch(
        PipelineDefinition pipeline,
        IReadOnlyDictionary<string, IntegrationBatch> resolvedFetches,
        ParseNodeDefinition parseNode,
        string fetchNodeId)
    {
        if (resolvedFetches.TryGetValue(fetchNodeId, out var batch))
        {
            return batch;
        }

        throw new InvalidOperationException(
            $"Flow '{pipeline.Id}' parse node '{parseNode.Id}' declares fetch node '{fetchNodeId}', which is not a fetch node of this flow.");
    }

    /// <summary>
    /// Stamps every payload leaving a parse node with that parse node's id (under the <c>sourceId</c> metadata key,
    /// kept for compatibility with modules such as the multi-source join), so downstream fetch nodes and the join
    /// engine can tell the merged payloads apart.
    /// </summary>
    private static IntegrationBatch TagWithParseId(IntegrationBatch batch, string parseId)
    {
        return new IntegrationBatch(batch.Payloads.Select(payload =>
        {
            var metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
            {
                [SourceIdMetadataKey] = parseId
            };
            return payload with { Metadata = metadata };
        }).ToArray());
    }

    /// <summary>
    /// Merges several batches by simple payload concatenation. Fetch or augment modules that need more structure
    /// than a flat list should read <c>payload.Metadata["sourceId"]</c> to disambiguate which parse node each
    /// payload came from.
    /// </summary>
    private static IntegrationBatch MergeBatches(IEnumerable<IntegrationBatch> batches)
    {
        var payloads = new List<IntegrationPayload>();
        foreach (var batch in batches)
        {
            payloads.AddRange(batch.Payloads);
        }

        return payloads.Count == 0 ? IntegrationBatch.Empty : new IntegrationBatch(payloads);
    }

    private static bool IsSourceGraphStageId(string stageId)
        => stageId.StartsWith(FetchStagePrefix, StringComparison.Ordinal)
            || stageId.StartsWith(ParseStagePrefix, StringComparison.Ordinal);

    private async Task<FlowExecutionResult> RunAugmentOnwardAsync(
        PipelineDefinition pipeline, FlowExecutionContext context, DateTimeOffset startedAt, int augmentIndex, IntegrationBatch batch, int attemptsAlreadyMade, CancellationToken cancellationToken)
    {
        if (augmentIndex >= pipeline.Augments.Count)
        {
            return await RunDeliveryLoopAsync(
                pipeline, context, startedAt, batch, startRouteIndex: 0,
                resumeRenderInput: null, resumeRenderAttempts: 0, resumeDeliverInput: null, resumeDeliverAttempts: 0,
                committedAtomicDeliveries: [], cancellationToken).ConfigureAwait(false);
        }

        var augment = pipeline.Augments[augmentIndex];
        var outputBatch = await RunStageAsync(
            pipeline, context, startedAt, $"augment:{augmentIndex}", EffectiveRetry(pipeline, augment), attemptsAlreadyMade,
            batch, committedAtomicDeliveries: [], postAugmentBatchForSnapshot: null, resolvedFetchesForSnapshot: null, resolvedParsesForSnapshot: null,
            invoke: async ct =>
            {
                await using var augmentModule = await moduleCatalog.LeaseOrchestrationAugmentAsync(augment.Module, ct).ConfigureAwait(false);
                return await ProcessConditionalStepAsync(
                    batch, augment, applicableBatch => augmentModule.Module.AugmentAsync(context, applicableBatch, augment, ct), ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (context.Disposition == FlowExecutionDisposition.Suspended)
        {
            var suspendedAt = timeProvider.GetUtcNow();
            await flowRetryStateStore.DeleteAllForExecutionAsync(pipeline.Id, context.ExecutionId, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Flow {FlowId} execution {ExecutionId} suspended after augment {ModuleId}. Reason: {Reason}",
                pipeline.Id, context.ExecutionId, augment.Module, context.SuspensionReason ?? "n/a");
            return new FlowExecutionResult(pipeline.Id, startedAt, suspendedAt, outputBatch.Count, [], FlowExecutionOutcome.Suspended, context.SuspensionReason);
        }

        return await RunAugmentOnwardAsync(pipeline, context, startedAt, augmentIndex + 1, outputBatch, attemptsAlreadyMade: 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FlowExecutionResult> RunDeliveryLoopAsync(
        PipelineDefinition pipeline,
        FlowExecutionContext context,
        DateTimeOffset startedAt,
        IntegrationBatch postAugmentBatch,
        int startRouteIndex,
        IntegrationBatch? resumeRenderInput,
        int resumeRenderAttempts,
        IntegrationBatch? resumeDeliverInput,
        int resumeDeliverAttempts,
        List<FlowRetryCommittedDelivery> committedAtomicDeliveries,
        CancellationToken cancellationToken)
    {
        for (var routeIndex = startRouteIndex; routeIndex < pipeline.Deliveries.Count; routeIndex++)
        {
            var delivery = pipeline.Deliveries[routeIndex];
            var isResumedRoute = routeIndex == startRouteIndex;

            try
            {
                IntegrationBatch renderedBatch;
                if (isResumedRoute && resumeDeliverInput is { } resumedDeliverBatch)
                {
                    // The render sub-stage already succeeded before the crash/failure; jump straight to deliver.
                    renderedBatch = resumedDeliverBatch;
                }
                else
                {
                    var renderApplicableBatch = isResumedRoute && resumeRenderInput is { } resumedRenderBatch
                        ? resumedRenderBatch
                        : SplitBatch(postAugmentBatch, delivery.Render).ApplicableBatch;

                    if (renderApplicableBatch.Count == 0)
                    {
                        continue;
                    }

                    var renderAttempts = isResumedRoute ? resumeRenderAttempts : 0;
                    renderedBatch = await RunStageAsync(
                        pipeline, context, startedAt, $"delivery:{routeIndex}:render", EffectiveRetry(pipeline, delivery.Render), renderAttempts,
                        renderApplicableBatch, committedAtomicDeliveries, postAugmentBatchForSnapshot: postAugmentBatch, resolvedFetchesForSnapshot: null, resolvedParsesForSnapshot: null,
                        invoke: async ct =>
                        {
                            await using var renderModule = await moduleCatalog.LeaseRenderAsync(delivery.Render.Module, ct).ConfigureAwait(false);
                            return await renderModule.Module.RenderAsync(context, renderApplicableBatch, delivery.Render, ct).ConfigureAwait(false);
                        },
                        cancellationToken).ConfigureAwait(false);
                }

                var deliverApplicableBatch = SplitBatch(renderedBatch, delivery.Deliver).ApplicableBatch;
                if (deliverApplicableBatch.Count == 0)
                {
                    continue;
                }

                var deliverAttempts = isResumedRoute && resumeDeliverInput is not null ? resumeDeliverAttempts : 0;
                await RunStageAsync(
                    pipeline, context, startedAt, $"delivery:{routeIndex}:deliver", EffectiveRetry(pipeline, delivery.Deliver), deliverAttempts,
                    deliverApplicableBatch, committedAtomicDeliveries, postAugmentBatchForSnapshot: postAugmentBatch, resolvedFetchesForSnapshot: null, resolvedParsesForSnapshot: null,
                    invoke: async ct =>
                    {
                        await using var deliverModule = await moduleCatalog.LeaseDeliverAsync(delivery.Deliver.Module, ct).ConfigureAwait(false);
                        if (ExpectsResponse(delivery.Deliver) && deliverModule.Module is IRequestResponseDeliverModule requestResponseModule)
                        {
                            var response = await requestResponseModule.DeliverAndCaptureResponseAsync(context, deliverApplicableBatch, delivery.Deliver, ct).ConfigureAwait(false);
                            context.CaptureResponse(response);
                        }
                        else
                        {
                            await deliverModule.Module.DeliverAsync(context, deliverApplicableBatch, delivery.Deliver, ct).ConfigureAwait(false);
                        }

                        return deliverApplicableBatch;
                    },
                    cancellationToken).ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(delivery.AtomicScope))
                {
                    committedAtomicDeliveries.Add(new FlowRetryCommittedDelivery
                    {
                        RouteIndex = routeIndex,
                        AtomicScope = delivery.AtomicScope!,
                        CompensationModule = delivery.Compensation?.Module,
                        CompensationSettings = delivery.Compensation is null
                            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                            : new Dictionary<string, string>(delivery.Compensation.Settings, StringComparer.OrdinalIgnoreCase),
                        DeliveredPayloads = SnapshotBatch(deliverApplicableBatch)
                    });
                }
            }
            catch (FlowRetryScheduledSignal)
            {
                // Not a genuine failure - a retry has already been persisted; bubble straight up without compensating.
                throw;
            }
            catch (Exception exception) when (!string.IsNullOrWhiteSpace(delivery.AtomicScope))
            {
                logger.LogError(
                    exception,
                    "Flow {FlowId} execution {ExecutionId} delivery route {RouteIndex} ({Module}) failed permanently inside atomic scope {Scope}; compensating prior committed deliveries in that scope.",
                    pipeline.Id, context.ExecutionId, routeIndex, delivery.Deliver.Module, delivery.AtomicScope);
                await CompensateAtomicScopeAsync(pipeline, context, committedAtomicDeliveries, delivery.AtomicScope!, cancellationToken).ConfigureAwait(false);
                await flowRetryStateStore.DeleteAllForExecutionAsync(pipeline.Id, context.ExecutionId, cancellationToken).ConfigureAwait(false);
                throw;
            }
        }

        if (context.PendingCheckpointCompletion is { } pendingCompletion)
        {
            await orchestrationStateStore.DeleteAsync(pendingCompletion.FlowId, pendingCompletion.CorrelationKey, cancellationToken).ConfigureAwait(false);
        }

        await flowRetryStateStore.DeleteAllForExecutionAsync(pipeline.Id, context.ExecutionId, cancellationToken).ConfigureAwait(false);

        var completedAt = timeProvider.GetUtcNow();
        logger.LogInformation(
            "Flow {FlowId} execution {ExecutionId} completed with {PayloadCount} payload(s).",
            pipeline.Id, context.ExecutionId, postAugmentBatch.Count);

        return new FlowExecutionResult(
            pipeline.Id,
            startedAt,
            completedAt,
            postAugmentBatch.Count,
            pipeline.Deliveries.Select(static route => route.Deliver.Module).ToArray(),
            FlowExecutionOutcome.Completed,
            null,
            context.CapturedResponses.Count > 0 ? context.CapturedResponses : null);
    }

    /// <summary>
    /// Runs a single stage attempt. On failure, either persists a <see cref="FlowRetryState"/> and throws
    /// <see cref="FlowRetryScheduledSignal"/> (if the effective retry policy allows another attempt) or clears
    /// any stale retry row for this stage and rethrows the original exception (once attempts are exhausted).
    /// On success, clears any stale retry row left over from a prior failed attempt at this same stage.
    /// </summary>
    private async Task<IntegrationBatch> RunStageAsync(
        PipelineDefinition pipeline,
        FlowExecutionContext context,
        DateTimeOffset executionStartedAt,
        string stageId,
        RetryPolicyDefinition retryPolicy,
        int attemptsAlreadyMade,
        IntegrationBatch inputBatch,
        IReadOnlyList<FlowRetryCommittedDelivery> committedAtomicDeliveries,
        IntegrationBatch? postAugmentBatchForSnapshot,
        IReadOnlyDictionary<string, IntegrationBatch>? resolvedFetchesForSnapshot,
        IReadOnlyDictionary<string, IntegrationBatch>? resolvedParsesForSnapshot,
        Func<CancellationToken, Task<IntegrationBatch>> invoke,
        CancellationToken cancellationToken)
    {
        var attemptNumber = attemptsAlreadyMade + 1;
        IntegrationBatch output;

        try
        {
            output = await invoke(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (retryPolicy.AllowsAnotherAttempt(attemptNumber))
            {
                var nextAttemptAt = timeProvider.GetUtcNow().Add(retryPolicy.ComputeDelay(attemptNumber + 1));
                var retryState = new FlowRetryState
                {
                    FlowId = pipeline.Id,
                    ExecutionId = context.ExecutionId,
                    StageId = stageId,
                    AttemptsMade = attemptNumber,
                    NextAttemptAt = nextAttemptAt,
                    LastError = exception.Message,
                    ExecutionStartedAt = executionStartedAt,
                    InputPayloads = SnapshotBatch(inputBatch),
                    ResolvedFetchPayloads = SnapshotResolvedBatches(resolvedFetchesForSnapshot),
                    ResolvedParsePayloads = SnapshotResolvedBatches(resolvedParsesForSnapshot),
                    PostAugmentPayloads = postAugmentBatchForSnapshot is null ? null : SnapshotBatch(postAugmentBatchForSnapshot),
                    CommittedAtomicDeliveries = committedAtomicDeliveries.ToList()
                };
                await flowRetryStateStore.UpsertAsync(retryState, cancellationToken).ConfigureAwait(false);
                logger.LogWarning(
                    exception,
                    "Flow {FlowId} execution {ExecutionId} stage {StageId} failed on attempt {Attempt}; attempt {NextAttempt} scheduled for {NextAttemptAt}.",
                    pipeline.Id, context.ExecutionId, stageId, attemptNumber, attemptNumber + 1, nextAttemptAt);

                throw new FlowRetryScheduledSignal(new FlowExecutionResult(
                    pipeline.Id,
                    executionStartedAt,
                    timeProvider.GetUtcNow(),
                    inputBatch.Count,
                    [],
                    FlowExecutionOutcome.Retrying,
                    $"Stage '{stageId}' failed on attempt {attemptNumber}: {exception.Message}",
                    null,
                    nextAttemptAt));
            }

            await flowRetryStateStore.DeleteAsync(pipeline.Id, context.ExecutionId, stageId, cancellationToken).ConfigureAwait(false);
            logger.LogError(
                exception,
                "Flow {FlowId} execution {ExecutionId} stage {StageId} permanently failed after {Attempts} attempt(s).",
                pipeline.Id, context.ExecutionId, stageId, attemptNumber);
            throw;
        }

        if (attemptsAlreadyMade > 0)
        {
            await flowRetryStateStore.DeleteAsync(pipeline.Id, context.ExecutionId, stageId, cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Flow {FlowId} execution {ExecutionId} stage {StageId} succeeded on attempt {Attempt} after a prior failure.",
                pipeline.Id, context.ExecutionId, stageId, attemptNumber);
        }

        return output;
    }

    private static bool ExpectsResponse(ModuleStepDefinition step)
        => step.Settings.TryGetValue(ExpectsResponseSetting, out var value)
            && bool.TryParse(value, out var expectsResponse)
            && expectsResponse;

    private static async Task<IntegrationBatch> ProcessConditionalStepAsync(
        IntegrationBatch batch,
        ModuleStepDefinition step,
        Func<IntegrationBatch, Task<IntegrationBatch>> processor,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (applicableBatch, passthroughBatch) = SplitBatch(batch, step);
        if (applicableBatch.Count == 0)
        {
            return batch;
        }

        var processedBatch = await processor(applicableBatch).ConfigureAwait(false);
        return passthroughBatch.Count == 0
            ? processedBatch
            : new IntegrationBatch(processedBatch.Payloads.Concat(passthroughBatch.Payloads).ToArray());
    }

    private static (IntegrationBatch ApplicableBatch, IntegrationBatch PassthroughBatch) SplitBatch(IntegrationBatch batch, ModuleStepDefinition step)
    {
        if (!step.Settings.TryGetValue(ConditionJsonSetting, out var conditionJson) || string.IsNullOrWhiteSpace(conditionJson))
        {
            return (batch, IntegrationBatch.Empty);
        }

        var conditions = DecisionRuntime.ParseConditions(conditionJson, step.Module);
        if (conditions.Count == 0)
        {
            return (batch, IntegrationBatch.Empty);
        }

        var applicable = new List<IntegrationPayload>(batch.Payloads.Count);
        var passthrough = new List<IntegrationPayload>(batch.Payloads.Count);

        foreach (var payload in batch.Payloads)
        {
            if (DecisionRuntime.MatchesAll(payload, conditions, step.Module))
            {
                applicable.Add(payload);
            }
            else
            {
                passthrough.Add(payload);
            }
        }

        return (new IntegrationBatch(applicable), new IntegrationBatch(passthrough));
    }

    private PipelineDefinition GetConfiguredFlow(string flowId)
    {
        var pipeline = flowDefinitionService.GetById(flowId);

        if (!pipeline.Enabled)
        {
            throw new InvalidOperationException($"Flow '{flowId}' is disabled.");
        }

        if (pipeline.Fetches.Count == 0 || pipeline.Parses.Count == 0)
        {
            throw new InvalidOperationException($"Flow '{flowId}' does not define any fetch and parse nodes.");
        }

        foreach (var fetchNode in pipeline.Fetches)
        {
            if (string.IsNullOrWhiteSpace(fetchNode.Fetch.Module))
            {
                throw new InvalidOperationException($"Flow '{flowId}' fetch node '{fetchNode.Id}' does not define a fetch module.");
            }
        }

        foreach (var parseNode in pipeline.Parses)
        {
            if (string.IsNullOrWhiteSpace(parseNode.Parse.Module))
            {
                throw new InvalidOperationException($"Flow '{flowId}' parse node '{parseNode.Id}' does not define a parse module.");
            }
        }

        // Resolve {{config:...}}/{{secret:...}} placeholder tokens (generated by the BizTalk importer or
        // hand-authored) against the stored config value store. Throws with a clear, actionable message
        // naming every unresolved reference rather than letting a module see a literal placeholder string.
        return configValueResolver.Resolve(pipeline);
    }
}
