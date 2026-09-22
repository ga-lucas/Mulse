namespace Service.Models;

/// <summary>Represents one BizTalk artifact discovered during migration analysis.</summary>
/// <param name="RelativePath">Display-friendly path relative to the artifact's own project's scope root.</param>
/// <param name="FullPath">
/// The absolute path on disk. Prefer this over recombining <see cref="RelativePath"/> with a caller-provided
/// root directory: when artifacts are inherited from a referenced project in a different analyzed source
/// (see <c>AdditionalSourcePaths</c>), <see cref="RelativePath"/> is relative to that other project's own
/// scope root, not the current project's, so recombination with the wrong root silently produces a bad path.
/// </param>
public sealed record BizTalkArtifactResponse(
    string Kind,
    string Name,
    string RelativePath,
    string FullPath);
