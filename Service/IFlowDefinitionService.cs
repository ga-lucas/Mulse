using Mulse.Modules;

namespace Service;

public interface IFlowDefinitionService
{
    IReadOnlyList<PipelineDefinition> GetAll();

    PipelineDefinition GetById(string flowId);

    Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken);

    Task<PipelineDefinition> UpdateAsync(string flowId, PipelineDefinition pipeline, CancellationToken cancellationToken);

    Task DeleteAsync(string flowId, CancellationToken cancellationToken);
}
