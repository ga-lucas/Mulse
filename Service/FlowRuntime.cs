using System.Collections.Concurrent;
using Mulse.Modules;

namespace Service;

public sealed class FlowRuntime(
    IFlowDefinitionService flowDefinitionService,
    IModuleCatalog moduleCatalog,
    IFlowOrchestrationStateStore orchestrationStateStore,
    TimeProvider timeProvider,
    ILogger<FlowRuntime> logger) : IFlowRuntime
{
    private const string ConditionJsonSetting = "conditionJson";
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
            var context = new FlowExecutionContext(
                pipeline.Id,
                Guid.CreateVersion7().ToString(),
                startedAt);

            await using var fetch = await moduleCatalog.LeaseFetchAsync(pipeline.Fetch.Module, cancellationToken).ConfigureAwait(false);
            var batch = await fetch.Module
                .FetchAsync(context, pipeline.Fetch, cancellationToken)
                .ConfigureAwait(false);

            await using var parse = await moduleCatalog.LeaseParseAsync(pipeline.Parse.Module, cancellationToken).ConfigureAwait(false);
            batch = await ProcessConditionalStepAsync(
                batch,
                pipeline.Parse,
                applicableBatch => parse.Module.ParseAsync(context, applicableBatch, pipeline.Parse, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            foreach (var augment in pipeline.Augments)
            {
                await using var augmentModule = await moduleCatalog.LeaseOrchestrationAugmentAsync(augment.Module, cancellationToken).ConfigureAwait(false);
                batch = await ProcessConditionalStepAsync(
                    batch,
                    augment,
                    applicableBatch => augmentModule.Module.AugmentAsync(context, applicableBatch, augment, cancellationToken),
                    cancellationToken).ConfigureAwait(false);

                if (context.Disposition == FlowExecutionDisposition.Suspended)
                {
                    var suspendedAt = timeProvider.GetUtcNow();
                    logger.LogInformation(
                        "Flow {FlowId} execution {ExecutionId} suspended after augment {ModuleId}. Reason: {Reason}",
                        pipeline.Id,
                        context.ExecutionId,
                        augment.Module,
                        context.SuspensionReason ?? "n/a");
                    return new FlowExecutionResult(
                        pipeline.Id,
                        startedAt,
                        suspendedAt,
                        batch.Count,
                        [],
                        FlowExecutionOutcome.Suspended,
                        context.SuspensionReason);
                }
            }

            foreach (var delivery in pipeline.Deliveries)
            {
                var (renderApplicable, _) = SplitBatch(batch, delivery.Render);
                if (renderApplicable.Count == 0)
                {
                    continue;
                }

                await using var renderModule = await moduleCatalog.LeaseRenderAsync(delivery.Render.Module, cancellationToken).ConfigureAwait(false);
                var renderedBatch = await renderModule.Module
                    .RenderAsync(context, renderApplicable, delivery.Render, cancellationToken)
                    .ConfigureAwait(false);

                var (deliverApplicable, _) = SplitBatch(renderedBatch, delivery.Deliver);
                if (deliverApplicable.Count == 0)
                {
                    continue;
                }

                await using var deliverModule = await moduleCatalog.LeaseDeliverAsync(delivery.Deliver.Module, cancellationToken).ConfigureAwait(false);
                await deliverModule.Module
                    .DeliverAsync(context, deliverApplicable, delivery.Deliver, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (context.PendingCheckpointCompletion is { } pendingCompletion)
            {
                await orchestrationStateStore.DeleteAsync(pendingCompletion.FlowId, pendingCompletion.CorrelationKey, cancellationToken).ConfigureAwait(false);
            }

            var completedAt = timeProvider.GetUtcNow();
            logger.LogInformation(
                "Flow {FlowId} execution {ExecutionId} completed with {PayloadCount} payload(s).",
                pipeline.Id,
                context.ExecutionId,
                batch.Count);

            return new FlowExecutionResult(
                pipeline.Id,
                startedAt,
                completedAt,
                batch.Count,
                pipeline.Deliveries.Select(static route => route.Deliver.Module).ToArray(),
                FlowExecutionOutcome.Completed,
                null);
        }
        finally
        {
            gate.Release();
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

        return pipeline;
    }
}
