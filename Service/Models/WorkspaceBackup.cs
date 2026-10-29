using System.ComponentModel.DataAnnotations;
using Mulse.Modules;

namespace Service.Models;

public sealed record WorkspaceBackupDocument(
    int Version,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PipelineDefinition> Flows,
    IReadOnlyList<ManagedModulePackageDefinition> Packages,
    IReadOnlyDictionary<string, string> ConfigValues);

public sealed record CreateWorkspaceBackupRequest([property: Required, MinLength(12)] string Password);

public sealed record RestoreWorkspaceRequest(
    [property: Required] string Content,
    [property: Required, MinLength(12)] string Password,
    [property: Required] string Mode,
    string? Confirmation = null);

public sealed record WorkspaceRestorePreview(
    int FlowCount,
    int PackageCount,
    int ConfigValueCount,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> Warnings);

public sealed record DeleteAllFlowsRequest([property: Required] string Confirmation);
