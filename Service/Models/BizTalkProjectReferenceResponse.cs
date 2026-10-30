using System.Text.Json.Serialization;

namespace Service.Models;

[JsonConverter(typeof(JsonStringEnumConverter<BizTalkReferenceResolution>))]
public enum BizTalkReferenceResolution
{
    ProjectPath,
    HintPath,
    UniqueName,
    Ambiguous,
    Unresolved,
    External
}

/// <summary>Source evidence and resolution of one reference within the analyzed workspace.</summary>
public sealed record BizTalkProjectReferenceResponse(
    string Name,
    string Include,
    string? HintPath,
    BizTalkReferenceResolution Resolution,
    string? ResolvedProjectPath,
    IReadOnlyList<string> CandidateProjectPaths,
    string Description);
