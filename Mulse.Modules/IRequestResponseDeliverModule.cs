namespace Mulse.Modules;

/// <summary>
/// Opt-in extension for deliver modules whose transport is inherently request-response
/// (e.g. a BizTalk two-way/solicit-response WCF send port). Implementing this interface lets
/// <see cref="FlowRuntime"/>-style runtimes capture the synchronous reply instead of discarding it,
/// which is required to faithfully migrate BizTalk two-way ports.
/// </summary>
public interface IRequestResponseDeliverModule : IDeliverModule
{
    /// <summary>
    /// Delivers the batch and returns the captured response payloads (one per request payload,
    /// in the same order) instead of discarding the reply.
    /// </summary>
    Task<IntegrationBatch> DeliverAndCaptureResponseAsync(
        FlowExecutionContext context,
        IntegrationBatch batch,
        ModuleStepDefinition step,
        CancellationToken cancellationToken);
}
