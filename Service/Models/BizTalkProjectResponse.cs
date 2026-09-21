namespace Service.Models;

/// <summary>Represents one BizTalk project and its major artifact counts.</summary>
public sealed record BizTalkProjectResponse(
    string Name,
    string ProjectPath,
    int OrchestrationCount,
    int MapCount,
    int SchemaCount,
    int PipelineCount,
    IReadOnlyList<string> ReferencedCustomAssemblies,
    IReadOnlyList<BizTalkArtifactResponse> Artifacts);
