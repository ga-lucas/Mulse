namespace Service.Tests;

/// <summary>Minimal in-memory <see cref="IFlowDefinitionService"/> double for <see cref="FlowRuntime"/> tests:
/// backed by a plain list rather than <see cref="IRuntimeConfigurationStore"/>, so tests can construct
/// arbitrary <see cref="PipelineDefinition"/> fixtures directly without going through validation.</summary>
internal sealed class FakeFlowDefinitionService(params PipelineDefinition[] pipelines) : IFlowDefinitionService
{
    private readonly List<PipelineDefinition> _pipelines = [.. pipelines];

    public IReadOnlyList<PipelineDefinition> GetAll() => _pipelines;

    public PipelineDefinition GetById(string flowId)
        => _pipelines.FirstOrDefault(pipeline => string.Equals(pipeline.Id, flowId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Flow '{flowId}' was not found.");

    public Task<PipelineDefinition> CreateAsync(PipelineDefinition pipeline, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<PipelineDefinition> UpdateAsync(string flowId, PipelineDefinition pipeline, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task DeleteAsync(string flowId, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
