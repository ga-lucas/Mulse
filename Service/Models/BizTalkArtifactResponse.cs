namespace Service.Models;

/// <summary>Represents one BizTalk artifact discovered during migration analysis.</summary>
public sealed record BizTalkArtifactResponse(
    string Kind,
    string Name,
    string RelativePath);
