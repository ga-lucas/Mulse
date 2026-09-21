namespace Service.Models;

/// <summary>Represents one custom .NET assembly that participates in a BizTalk integration application.</summary>
public sealed record BizTalkCustomAssemblyResponse(
    string Name,
    string ProjectPath,
    int SourceFileCount,
    string SuggestedModuleKind,
    string MigrationApproach,
    bool WrapperRecommended,
    IReadOnlyList<string> UsedByProjects);
