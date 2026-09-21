using Mulse.Modules;
using Service.Models;

namespace Service;

public interface IFlowDesignService
{
    Task<FlowDesignResponse> AnalyzeAsync(AnalyzeFlowDesignRequest request, CancellationToken cancellationToken);

    Task<PipelineDefinition> CreateFlowAsync(CreateDesignedFlowRequest request, CancellationToken cancellationToken);
}
