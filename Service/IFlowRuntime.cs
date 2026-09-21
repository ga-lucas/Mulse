using Mulse.Modules;

namespace Service;

public interface IFlowRuntime
{
    IReadOnlyList<PipelineDefinition> GetConfiguredFlows();

    Task<FlowExecutionResult> ExecuteAsync(string flowId, CancellationToken cancellationToken);
}
