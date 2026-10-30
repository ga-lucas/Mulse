namespace Mulse.Modules.Pipeline;

public sealed record PipelineMigrationInventory(
    string Direction,
    int TotalStages,
    int OmittedStageCount,
    int TotalComponents,
    int OmittedComponentCount,
    IReadOnlyList<PipelineStageEvidence> Stages);

public sealed record PipelineStageEvidence(
    int Order,
    int SourceLine,
    string CategoryId,
    string StageName,
    IReadOnlyList<PipelineComponentEvidence> Components);

public sealed record PipelineComponentEvidence(
    int Order,
    int SourceLine,
    string ClassName,
    string Version,
    int TotalProperties,
    int OmittedPropertyCount,
    IReadOnlyList<string> PropertyNames);
