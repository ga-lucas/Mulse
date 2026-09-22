using Mulse.Modules;

namespace Service.Execution;

public sealed partial class FlowRuntime
{
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
}
