using System.Collections.Concurrent;
using Mulse.Modules;

namespace Service;

public sealed class FlowRuntime(
    IFlowDefinitionService flowDefinitionService,
    IModuleCatalog moduleCatalog,
    TimeProvider timeProvider,
    ILogger<FlowRuntime> logger) : IFlowRuntime
{
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

            await using var input = await moduleCatalog.LeaseInputAsync(pipeline.Input.Module, cancellationToken).ConfigureAwait(false);
            var batch = await input.Module
                .ReadAsync(context, pipeline.Input, cancellationToken)
                .ConfigureAwait(false);

            foreach (var augment in pipeline.Augments)
            {
                await using var augmentModule = await moduleCatalog.LeaseOrchestrationAugmentAsync(augment.Module, cancellationToken).ConfigureAwait(false);
                batch = await augmentModule.Module
                    .AugmentAsync(context, batch, augment, cancellationToken)
                    .ConfigureAwait(false);
            }

            foreach (var output in pipeline.Outputs)
            {
                await using var outputModule = await moduleCatalog.LeaseOutputAsync(output.Module, cancellationToken).ConfigureAwait(false);
                await outputModule.Module
                    .WriteAsync(context, batch, output, cancellationToken)
                    .ConfigureAwait(false);
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
                pipeline.Outputs.Select(static step => step.Module).ToArray());
        }
        finally
        {
            gate.Release();
        }
    }

    private PipelineDefinition GetConfiguredFlow(string flowId)
    {
        var pipeline = flowDefinitionService.GetById(flowId);

        if (!pipeline.Enabled)
        {
            throw new InvalidOperationException($"Flow '{flowId}' is disabled.");
        }

        if (string.IsNullOrWhiteSpace(pipeline.Input.Module))
        {
            throw new InvalidOperationException($"Flow '{flowId}' does not define an input module.");
        }

        return pipeline;
    }
}
