using System.Collections.Concurrent;
using Mulse.Modules;

namespace Service;

/// <summary>
/// Executes configured flows as a sequence of named, independently retryable "stages": <c>fetch</c>,
/// <c>parse</c>, <c>augment:{index}</c>, and per delivery route <c>delivery:{route}:render</c> /
/// <c>delivery:{route}:deliver</c>. Each stage's effective <see cref="RetryPolicyDefinition"/> (step override,
/// falling back to the flow-level default, falling back to a single attempt) governs what happens when that
/// stage throws: if another attempt is allowed, the stage's input batch (and, for delivery stages, the shared
/// post-augment batch) is snapshotted to disk via <see cref="IFlowRetryStateStore"/> and execution returns
/// immediately with a <see cref="FlowExecutionOutcome.Retrying"/> result; a separate background driver is
/// responsible for waiting out the delay and calling <see cref="ResumeRetryAsync"/>. Because resumption reads
/// the same durable state a crash would have left behind, this also provides crash/restart recovery for free.
/// </summary>
public sealed class FlowRuntime(
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
                return await RunFetchOnwardAsync(pipeline, context, startedAt, attemptsAlreadyMade: 0, cancellationToken).ConfigureAwait(false);
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
    /// Resumes a previously persisted <see cref="FlowRetryState"/>, either because its delay has elapsed or
    /// because the service is recovering pending work found on disk after a restart. Returns <c>null</c> (after
    /// deleting the stale retry entry) if the flow has since been deleted or disabled.
    /// </summary>
    public async Task<FlowExecutionResult?> ResumeRetryAsync(FlowRetryState retryState, CancellationToken cancellationToken)
    {
        PipelineDefinition pipeline;
        try
        {
            pipeline = GetConfiguredFlow(retryState.FlowId);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning(
                "Abandoning pending retry for flow {FlowId} execution {ExecutionId} stage {StageId}: {Reason}",
                retryState.FlowId, retryState.ExecutionId, retryState.StageId, exception.Message);
            await flowRetryStateStore.DeleteAllForExecutionAsync(retryState.FlowId, retryState.ExecutionId, cancellationToken).ConfigureAwait(false);
            return null;
        }

        var gate = _flowLocks.GetOrAdd(pipeline.Id, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = new FlowExecutionContext(pipeline.Id, retryState.ExecutionId, retryState.ExecutionStartedAt);
            var committedAtomicDeliveries = new List<FlowRetryCommittedDelivery>(retryState.CommittedAtomicDeliveries);
            var inputBatch = RestoreBatch(retryState.InputPayloads);

            try
            {
                if (string.Equals(retryState.StageId, "fetch", StringComparison.Ordinal))
                {
                    return await RunFetchOnwardAsync(pipeline, context, retryState.ExecutionStartedAt, retryState.AttemptsMade, cancellationToken).ConfigureAwait(false);
                }

                if (string.Equals(retryState.StageId, "parse", StringComparison.Ordinal))
                {
                    return await RunParseOnwardAsync(pipeline, context, retryState.ExecutionStartedAt, inputBatch, retryState.AttemptsMade, cancellationToken).ConfigureAwait(false);
                }

                if (retryState.StageId.StartsWith("augment:", StringComparison.Ordinal))
                {
                    var augmentIndex = int.Parse(retryState.StageId["augment:".Length..]);
                    return await RunAugmentOnwardAsync(pipeline, context, retryState.ExecutionStartedAt, augmentIndex, inputBatch, retryState.AttemptsMade, cancellationToken).ConfigureAwait(false);
                }

                var parts = retryState.StageId.Split(':');
                var routeIndex = int.Parse(parts[1]);
                var postAugmentBatch = RestoreBatch(retryState.PostAugmentPayloads ?? []);

                return string.Equals(parts[2], "render", StringComparison.Ordinal)
                    ? await RunDeliveryLoopAsync(
                        pipeline, context, retryState.ExecutionStartedAt, postAugmentBatch, routeIndex,
                        resumeRenderInput: inputBatch, resumeRenderAttempts: retryState.AttemptsMade,
                        resumeDeliverInput: null, resumeDeliverAttempts: 0,
                        committedAtomicDeliveries, cancellationToken).ConfigureAwait(false)
                    : await RunDeliveryLoopAsync(
                        pipeline, context, retryState.ExecutionStartedAt, postAugmentBatch, routeIndex,
                        resumeRenderInput: null, resumeRenderAttempts: 0,
                        resumeDeliverInput: inputBatch, resumeDeliverAttempts: retryState.AttemptsMade,
                        committedAtomicDeliveries, cancellationToken).ConfigureAwait(false);
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

    private async Task<FlowExecutionResult> RunFetchOnwardAsync(
        PipelineDefinition pipeline, FlowExecutionContext context, DateTimeOffset startedAt, int attemptsAlreadyMade, CancellationToken cancellationToken)
    {
        var fetchedBatch = await RunStageAsync(
            pipeline, context, startedAt, "fetch", EffectiveRetry(pipeline, pipeline.Fetch), attemptsAlreadyMade,
            IntegrationBatch.Empty, committedAtomicDeliveries: [], postAugmentBatchForSnapshot: null,
            invoke: async ct =>
            {
                await using var fetch = await moduleCatalog.LeaseFetchAsync(pipeline.Fetch.Module, ct).ConfigureAwait(false);
                return await fetch.Module.FetchAsync(context, pipeline.Fetch, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return await RunParseOnwardAsync(pipeline, context, startedAt, fetchedBatch, attemptsAlreadyMade: 0, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FlowExecutionResult> RunParseOnwardAsync(
        PipelineDefinition pipeline, FlowExecutionContext context, DateTimeOffset startedAt, IntegrationBatch fetchedBatch, int attemptsAlreadyMade, CancellationToken cancellationToken)
    {
        var parsedBatch = await RunStageAsync(
            pipeline, context, startedAt, "parse", EffectiveRetry(pipeline, pipeline.Parse), attemptsAlreadyMade,
            fetchedBatch, committedAtomicDeliveries: [], postAugmentBatchForSnapshot: null,
            invoke: async ct =>
            {
                await using var parse = await moduleCatalog.LeaseParseAsync(pipeline.Parse.Module, ct).ConfigureAwait(false);
                return await ProcessConditionalStepAsync(
                    fetchedBatch, pipeline.Parse, applicableBatch => parse.Module.ParseAsync(context, applicableBatch, pipeline.Parse, ct), ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        return await RunAugmentOnwardAsync(pipeline, context, startedAt, augmentIndex: 0, parsedBatch, attemptsAlreadyMade: 0, cancellationToken).ConfigureAwait(false);
    }

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
            batch, committedAtomicDeliveries: [], postAugmentBatchForSnapshot: null,
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
                        renderApplicableBatch, committedAtomicDeliveries, postAugmentBatchForSnapshot: postAugmentBatch,
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
                    deliverApplicableBatch, committedAtomicDeliveries, postAugmentBatchForSnapshot: postAugmentBatch,
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

    private static RetryPolicyDefinition EffectiveRetry(PipelineDefinition pipeline, ModuleStepDefinition step)
        => step.Retry ?? pipeline.Retry ?? RetryPolicyDefinition.NoRetry;

    private static List<FlowOrchestrationPayloadSnapshot> SnapshotBatch(IntegrationBatch batch)
        => batch.Payloads.Select(payload => new FlowOrchestrationPayloadSnapshot
        {
            Name = payload.Name,
            ContentBase64 = Convert.ToBase64String(payload.Content.ToArray()),
            ContentType = payload.ContentType,
            Metadata = new Dictionary<string, string>(payload.Metadata, StringComparer.OrdinalIgnoreCase)
        }).ToList();

    private static IntegrationBatch RestoreBatch(IReadOnlyList<FlowOrchestrationPayloadSnapshot> snapshots)
        => new(snapshots.Select(snapshot => new IntegrationPayload(
            snapshot.Name,
            BinaryData.FromBytes(Convert.FromBase64String(snapshot.ContentBase64)),
            snapshot.ContentType,
            new Dictionary<string, string>(snapshot.Metadata, StringComparer.OrdinalIgnoreCase))).ToArray());

    private static bool ExpectsResponse(ModuleStepDefinition step)
        => step.Settings.TryGetValue(ExpectsResponseSetting, out var value)
            && bool.TryParse(value, out var expectsResponse)
            && expectsResponse;

    /// <summary>
    /// Compensates previously committed delivery routes that share the given atomic scope, in reverse
    /// (last-committed-first) order, mirroring BizTalk orchestration <c>AtomicTransaction</c> scope rollback.
    /// Uses <see cref="CancellationToken.None"/> so cleanup still runs even if the triggering token is already
    /// cancelled. A route with no compensation module configured is logged and skipped rather than failing the
    /// whole rollback; one compensation failure does not stop attempts to compensate the remaining routes.
    /// </summary>
    private async Task CompensateAtomicScopeAsync(
        PipelineDefinition pipeline,
        FlowExecutionContext context,
        List<FlowRetryCommittedDelivery> committedAtomicDeliveries,
        string atomicScope,
        CancellationToken cancellationToken)
    {
        var toCompensate = committedAtomicDeliveries
            .Where(committed => string.Equals(committed.AtomicScope, atomicScope, StringComparison.Ordinal))
            .Reverse()
            .ToArray();

        foreach (var committed in toCompensate)
        {
            if (string.IsNullOrWhiteSpace(committed.CompensationModule))
            {
                logger.LogWarning(
                    "Flow {FlowId} execution {ExecutionId} cannot compensate delivery route {RouteIndex} in atomic scope {Scope}: no compensation module is configured for this route.",
                    pipeline.Id, context.ExecutionId, committed.RouteIndex, atomicScope);
                continue;
            }

            try
            {
                var compensationStep = new ModuleStepDefinition
                {
                    Module = committed.CompensationModule,
                    Settings = new Dictionary<string, string>(committed.CompensationSettings, StringComparer.OrdinalIgnoreCase)
                };
                var deliveredBatch = RestoreBatch(committed.DeliveredPayloads);
                await using var compensationModule = await moduleCatalog.LeaseDeliverAsync(committed.CompensationModule, CancellationToken.None).ConfigureAwait(false);
                await compensationModule.Module.DeliverAsync(context, deliveredBatch, compensationStep, CancellationToken.None).ConfigureAwait(false);
                logger.LogInformation(
                    "Flow {FlowId} execution {ExecutionId} compensated delivery route {RouteIndex} in atomic scope {Scope} using {CompensationModule}.",
                    pipeline.Id, context.ExecutionId, committed.RouteIndex, atomicScope, committed.CompensationModule);
            }
            catch (Exception compensationException)
            {
                logger.LogError(
                    compensationException,
                    "Flow {FlowId} execution {ExecutionId} compensation via {CompensationModule} failed for delivery route {RouteIndex} in atomic scope {Scope}.",
                    pipeline.Id, context.ExecutionId, committed.CompensationModule, committed.RouteIndex, atomicScope);
            }
        }
    }

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

        if (string.IsNullOrWhiteSpace(pipeline.Fetch.Module))
        {
            throw new InvalidOperationException($"Flow '{flowId}' does not define a fetch module.");
        }

        if (string.IsNullOrWhiteSpace(pipeline.Parse.Module))
        {
            throw new InvalidOperationException($"Flow '{flowId}' does not define a parse module.");
        }

        // Resolve {{config:...}}/{{secret:...}} placeholder tokens (generated by the BizTalk importer or
        // hand-authored) against the stored config value store. Throws with a clear, actionable message
        // naming every unresolved reference rather than letting a module see a literal placeholder string.
        return configValueResolver.Resolve(pipeline);
    }

    /// <summary>Control-flow-only signal: a retry was scheduled and persisted, so this is not a genuine failure.</summary>
    private sealed class FlowRetryScheduledSignal(FlowExecutionResult result) : Exception
    {
        public FlowExecutionResult Result { get; } = result;
    }
}
